using Cambrian.Application.DTOs.PlayCounts;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cambrian.Persistence.Services;

/// <inheritdoc cref="IPlayCountReconciliationService"/>
public sealed class PlayCountReconciliationService : IPlayCountReconciliationService
{
    private readonly CambrianDbContext _db;
    private readonly ILogger<PlayCountReconciliationService> _logger;

    public PlayCountReconciliationService(CambrianDbContext db, ILogger<PlayCountReconciliationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ReconciliationRunResult> ReconcileAsync(ReconciliationOptions options, CancellationToken ct = default)
    {
        var batchSize = Math.Clamp(options.BatchSize, 1, 5000);
        // Dry-run is authoritative over repair: even if a caller passes both DryRun=true and
        // Repair=true, nothing is written. Repair only ever takes effect on an explicit non-dry run.
        var effectiveRepair = !options.DryRun && options.Repair;

        var run = new PlayCountReconciliationRun
        {
            Id = Guid.NewGuid(),
            StartedAtUtc = DateTime.UtcNow,
            DryRun = options.DryRun,
            RepairRequested = options.Repair,
            RequestedBy = string.IsNullOrWhiteSpace(options.RequestedBy) ? "system" : options.RequestedBy,
            Scope = options.TrackIds is { Count: > 0 } ids ? $"{ids.Count} tracks" : "all",
            BatchSize = batchSize,
            Status = "running",
        };
        _db.PlayCountReconciliationRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        var mismatches = new List<ReconciliationMismatch>();

        try
        {
            await foreach (var batch in EnumerateBatchesAsync(options.TrackIds, batchSize, ct))
            {
                // Run-progress bookkeeping is updated and saved as part of THIS SAME batch's
                // final SaveChanges (inside ReconcileBatchAsync) — not a separate round-trip
                // afterward — so an interruption can never leave "data repaired, but the audit
                // row doesn't know it" as two different truths.
                var batchMismatches = await ReconcileBatchAsync(run, batch, effectiveRepair, ct);
                mismatches.AddRange(batchMismatches);

                _logger.LogInformation(
                    "EVENT: PlayCountReconciliationBatch runId:{RunId} scanned:{Scanned} mismatches:{Mismatches} repaired:{Repaired}",
                    run.Id, run.TracksScanned, run.MismatchesFound, run.MismatchesRepaired);
            }

            run.Status = "completed";
            run.CompletedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Cooperative interruption (worker shutdown, request abort). The run row keeps
            // whatever progress its already-committed batches wrote and stays "running" — an
            // honest partial record, not a fabricated "completed". Nothing durable is lost: a
            // later run (scheduled or manual) rescans and converges on the same correct answer.
            _logger.LogWarning(
                "EVENT: PlayCountReconciliationInterrupted runId:{RunId} scanned:{Scanned}",
                run.Id, run.TracksScanned);
            throw;
        }
        catch (Exception ex)
        {
            run.Status = "failed";
            run.ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            run.CompletedAtUtc = DateTime.UtcNow;
            // Deliberately not passing `ct` here: if the run failed for a reason unrelated to
            // cancellation, this row update must still land so the failure is auditable.
            await _db.SaveChangesAsync(CancellationToken.None);
            _logger.LogError(ex, "EVENT: PlayCountReconciliationFailed runId:{RunId}", run.Id);
            throw;
        }

        return new ReconciliationRunResult
        {
            RunId = run.Id,
            DryRun = options.DryRun,
            TracksScanned = run.TracksScanned,
            MismatchesFound = run.MismatchesFound,
            MismatchesRepaired = run.MismatchesRepaired,
            Status = run.Status,
            Mismatches = mismatches,
        };
    }

    private async IAsyncEnumerable<List<Guid>> EnumerateBatchesAsync(
        IReadOnlyCollection<Guid>? explicitTrackIds, int batchSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (explicitTrackIds is { Count: > 0 })
        {
            var idList = explicitTrackIds.Distinct().ToList();
            for (var offset = 0; offset < idList.Count; offset += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                yield return idList.Skip(offset).Take(batchSize).ToList();
            }
            yield break;
        }

        var pageOffset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await _db.Tracks.AsNoTracking()
                .OrderBy(t => t.Id)
                .Skip(pageOffset)
                .Take(batchSize)
                .Select(t => t.Id)
                .ToListAsync(ct);

            if (page.Count == 0)
                yield break;

            yield return page;

            if (page.Count < batchSize)
                yield break; // last page

            pageOffset += batchSize;
        }
    }

    private async Task<List<ReconciliationMismatch>> ReconcileBatchAsync(
        PlayCountReconciliationRun run, IReadOnlyList<Guid> trackIds, bool repair, CancellationToken ct)
    {
        var mismatches = new List<ReconciliationMismatch>();
        if (trackIds.Count == 0)
            return mismatches;

        void RecordProgress()
        {
            run.TracksScanned += trackIds.Count;
            run.MismatchesFound += mismatches.Count;
            run.MismatchesRepaired += mismatches.Count(m => m.Repaired);
        }

        // Computed truth, straight from durable qualified events — never from the projection.
        var computed = await _db.StreamSessions.AsNoTracking()
            .Where(s => trackIds.Contains(s.TrackId) && s.Qualified)
            .GroupBy(s => s.TrackId)
            .Select(g => new
            {
                TrackId = g.Key,
                PlayCount = (long)g.Count(),
                LastPlayedAt = (DateTime?)g.Max(s => s.StartedAt),
            })
            .ToDictionaryAsync(x => x.TrackId, ct);

        var computedUnique = await _db.StreamSessions.AsNoTracking()
            .Where(s => trackIds.Contains(s.TrackId) && s.Qualified)
            .Select(s => new { s.TrackId, Identity = s.UserId ?? s.AnonymousKey })
            .Distinct()
            .GroupBy(x => x.TrackId)
            .Select(g => new { TrackId = g.Key, Count = (long)g.Count() })
            .ToDictionaryAsync(x => x.TrackId, x => x.Count, ct);

        var stored = await _db.TrackStats
            .Where(s => trackIds.Contains(s.TrackId))
            .ToDictionaryAsync(s => s.TrackId, ct);

        var trackCreators = await _db.Tracks.AsNoTracking()
            .Where(t => trackIds.Contains(t.Id))
            .Select(t => new { t.Id, t.CreatorUuid })
            .ToDictionaryAsync(x => x.Id, x => x.CreatorUuid, ct);

        var now = DateTime.UtcNow;
        var affectedCreators = new HashSet<Guid>();

        foreach (var trackId in trackIds)
        {
            var computedPlay = computed.TryGetValue(trackId, out var c) ? c.PlayCount : 0L;
            var computedListeners = computedUnique.GetValueOrDefault(trackId);
            stored.TryGetValue(trackId, out var trackStat);
            var storedPlay = trackStat?.PlayCount ?? 0L;
            var storedListeners = trackStat?.UniqueListenerCount ?? 0L;

            if (storedPlay == computedPlay && storedListeners == computedListeners)
                continue; // no drift — nothing to record

            var repaired = false;
            if (repair)
            {
                if (trackStat is null)
                {
                    trackStat = new TrackStat { TrackId = trackId };
                    _db.TrackStats.Add(trackStat);
                }
                trackStat.PlayCount = computedPlay;
                trackStat.UniqueListenerCount = computedListeners;
                trackStat.UpdatedAt = now;
                if (computed.TryGetValue(trackId, out var computedRow) && computedRow.LastPlayedAt.HasValue)
                    trackStat.LastPlayedAt = computedRow.LastPlayedAt;
                repaired = true;

                if (trackCreators.TryGetValue(trackId, out var creatorUuid) && creatorUuid.HasValue)
                    affectedCreators.Add(creatorUuid.Value);
            }

            mismatches.Add(new ReconciliationMismatch(
                trackId, storedPlay, computedPlay, storedListeners, computedListeners, repaired));

            _db.PlayCountReconciliationEntries.Add(new PlayCountReconciliationEntry
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                TrackId = trackId,
                StoredPlayCount = storedPlay,
                ComputedPlayCount = computedPlay,
                StoredUniqueListenerCount = storedListeners,
                ComputedUniqueListenerCount = computedListeners,
                Repaired = repaired,
                CreatedAtUtc = now,
            });
        }

        if (repair && affectedCreators.Count > 0)
        {
            // Flush the TrackStats repairs first: RecomputeCreatorStatsAsync below re-reads
            // TrackStats with AsNoTracking, which goes straight to the database and would
            // otherwise miss whatever is still sitting unsaved in the change tracker.
            await _db.SaveChangesAsync(ct);
            await RecomputeCreatorStatsAsync(affectedCreators, ct);
        }

        // Record this batch's contribution to the run's bookkeeping in the SAME final save as
        // its data changes, so an interruption right after can never separate "the data was
        // repaired" from "the audit row says so".
        RecordProgress();
        await _db.SaveChangesAsync(ct);
        return mismatches;
    }

    /// <summary>
    /// Recomputes CreatorStats totals from the just-repaired TrackStats rows (already the
    /// corrected truth for the tracks touched this batch) rather than re-scanning StreamSessions
    /// again — cheaper, and exactly consistent with what this batch just wrote.
    /// </summary>
    private async Task RecomputeCreatorStatsAsync(IReadOnlyCollection<Guid> creatorUuids, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var totals = await _db.TrackStats.AsNoTracking()
            .Where(s => s.Track.CreatorUuid != null && creatorUuids.Contains(s.Track.CreatorUuid.Value))
            .GroupBy(s => s.Track.CreatorUuid!.Value)
            .Select(g => new
            {
                CreatorUuid = g.Key,
                TotalPlays = g.Sum(x => x.PlayCount),
                TotalUnique = g.Sum(x => x.UniqueListenerCount),
            })
            .ToDictionaryAsync(x => x.CreatorUuid, ct);

        var creatorStats = await _db.CreatorStats
            .Where(s => creatorUuids.Contains(s.CreatorId))
            .ToDictionaryAsync(s => s.CreatorId, ct);

        foreach (var creatorUuid in creatorUuids)
        {
            if (!creatorStats.TryGetValue(creatorUuid, out var creatorStat))
            {
                creatorStat = new CreatorStat { CreatorId = creatorUuid };
                _db.CreatorStats.Add(creatorStat);
            }

            var totalsForCreator = totals.GetValueOrDefault(creatorUuid);
            creatorStat.TotalPlays = totalsForCreator?.TotalPlays ?? 0L;
            creatorStat.UniqueListenerCount = totalsForCreator?.TotalUnique ?? 0L;
            creatorStat.UpdatedAt = now;
        }
    }
}
