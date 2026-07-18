using System.Data;
using System.Text.Json;
using Cambrian.Application.DTOs.Admin;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Observability;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Cambrian.Persistence.Services;

/// <summary>
/// Reconciles the append-only qualified-play ledger with TrackStats without
/// inventing historical events. LegacyPlayCount is an explicit frozen
/// baseline; only QualifiedPlayCount is rebuilt from QualifiedPlayEvents.
/// </summary>
public sealed class PlayReconciliationService : IPlayReconciliationService
{
    private const long PostgreSqlAdvisoryLockKey = 0x43414D42504C4159L; // "CAMBPLAY"
    private const double AggregationFreshnessSlaSeconds = 15;
    private const double ChartFreshnessSlaSeconds = 60;

    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CambrianDbContext _db;
    private readonly ILogger<PlayReconciliationService> _logger;

    public PlayReconciliationService(
        CambrianDbContext db,
        ILogger<PlayReconciliationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<PlayReconciliationReport> InspectAsync(
        PlayReconciliationRequest request,
        string requestingAdminId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAdminId(requestingAdminId);
        var trackIds = NormalizeTrackIds(
            request.TrackIds,
            PlayReconciliationRequest.MaximumTrackSelection,
            nameof(request.TrackIds));
        ValidateRange(
            request.MismatchLimit,
            1,
            PlayReconciliationRequest.MaximumMismatchLimit,
            nameof(request.MismatchLimit));

        var report = await BuildReportAsync(trackIds, request.MismatchLimit, ct);

        if (report.MismatchedTrackCount > 0)
            CambrianMetrics.PlayReconciliationMismatch.Add(report.MismatchedTrackCount);
        if (report.AggregationLagSeconds is double lag)
            CambrianMetrics.PlayAggregationLagSeconds.Record(lag);

        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = "play_reconciliation_dry_run",
            Admin = requestingAdminId,
            Timestamp = report.CheckedAtUtc,
            Details = JsonSerializer.Serialize(new
            {
                selectedTrackCount = report.SelectedTrackCount,
                report.QualifiedEventCount,
                report.MismatchedTrackCount,
                mismatchDetailsReturned = report.Mismatches.Count,
                report.DuplicateIdempotencyKeyGroupCount,
                report.DuplicatePlaybackSessionGroupCount,
                report.LegacyStreamSessionCount,
                report.PendingAggregationCount,
                report.OldestPendingQualifiedAtUtc,
                report.AggregationLagSeconds,
                report.StaleChartWindowCount,
                report.LegacyNonzeroTrendingScoreCount,
            }, AuditJsonOptions),
        });
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "EVENT: PlayReconciliationDryRun adminId:{AdminId} qualifiedEvents:{QualifiedEvents} mismatches:{Mismatches} pending:{Pending} duplicateKeys:{DuplicateKeys}",
            requestingAdminId,
            report.QualifiedEventCount,
            report.MismatchedTrackCount,
            report.PendingAggregationCount,
            report.DuplicateIdempotencyKeyGroupCount);

        return report;
    }

    public async Task<PlayReconciliationRepairResult> RepairAsync(
        PlayReconciliationRepairRequest request,
        string requestingAdminId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAdminId(requestingAdminId);
        var trackIds = NormalizeTrackIds(
            request.TrackIds,
            PlayReconciliationRepairRequest.MaximumTrackSelection,
            nameof(request.TrackIds));
        ValidateRange(
            request.TrackBatchSize,
            1,
            PlayReconciliationRepairRequest.MaximumTrackBatchSize,
            nameof(request.TrackBatchSize));
        ValidateRange(
            request.EventBatchSize,
            1,
            PlayReconciliationRepairRequest.MaximumEventBatchSize,
            nameof(request.EventBatchSize));

        var startedAtUtc = DateTime.UtcNow;
        await using IDbContextTransaction? transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            : null;

        var lockAcquired = await TryAcquireRepairLockAsync(ct);
        if (!lockAcquired)
        {
            var busyCompletedAtUtc = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                Action = "play_reconciliation_repair_busy",
                Admin = requestingAdminId,
                Timestamp = busyCompletedAtUtc,
                Details = JsonSerializer.Serialize(new
                {
                    status = "busy",
                    request.TrackBatchSize,
                    request.EventBatchSize,
                    selectedTrackCount = trackIds?.Count,
                }, AuditJsonOptions),
            });
            await _db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            _logger.LogWarning(
                "EVENT: PlayReconciliationRepairBusy adminId:{AdminId}",
                requestingAdminId);

            return new PlayReconciliationRepairResult(
                Status: "busy",
                LockAcquired: false,
                StartedAtUtc: startedAtUtc,
                CompletedAtUtc: busyCompletedAtUtc,
                TrackBatchSize: request.TrackBatchSize,
                EventBatchSize: request.EventBatchSize,
                CandidateTrackCount: 0,
                RepairedTrackCount: 0,
                EventsMarkedAggregated: 0,
                RepairedTrackIds: Array.Empty<Guid>(),
                RemainingMismatchedTrackCount: 0,
                RemainingPendingAggregationCount: 0,
                OldestPendingQualifiedAtUtc: null,
                AggregationLagSeconds: null);
        }

        var projection = await LoadProjectionAsync(trackIds, ct);
        var candidateTrackIds = GetCandidateTrackIds(projection)
            .OrderBy(id => id)
            .ToList();
        var selectedTrackIds = candidateTrackIds
            .Take(request.TrackBatchSize)
            .ToArray();

        await LockTrackStatsAsync(selectedTrackIds, ct);

        // Re-read after row locks are held. A concurrent acceptance either
        // committed before the lock (and is visible here), or waits and applies
        // its increment after this transaction commits.
        var selectedSet = selectedTrackIds.ToHashSet();
        var selectedProjection = await LoadProjectionAsync(selectedSet, ct);
        var now = DateTime.UtcNow;
        var trackedStats = await _db.TrackStats
            .Where(stat => selectedSet.Contains(stat.TrackId))
            .ToDictionaryAsync(stat => stat.TrackId, ct);

        foreach (var trackId in selectedTrackIds)
        {
            selectedProjection.Ledger.TryGetValue(trackId, out var ledger);
            var ledgerCount = ledger?.QualifiedCount ?? 0L;
            var latestQualifiedAtUtc = ledger?.LatestQualifiedAtUtc;

            if (!trackedStats.TryGetValue(trackId, out var stat))
            {
                stat = new TrackStat
                {
                    TrackId = trackId,
                    LegacyPlayCount = 0,
                };
                _db.TrackStats.Add(stat);
                trackedStats.Add(trackId, stat);
            }

            stat.QualifiedPlayCount = ledgerCount;
            stat.PlayCount = checked(stat.LegacyPlayCount + ledgerCount);
            if (latestQualifiedAtUtc is DateTime latest
                && (stat.LastPlayedAt is null || latest > stat.LastPlayedAt.Value))
            {
                stat.LastPlayedAt = latest;
            }
            stat.UpdatedAt = now;
            stat.ReconciledAtUtc = now;
        }

        var pendingEvents = await _db.QualifiedPlayEvents
            .Where(play => selectedSet.Contains(play.TrackId) && play.AggregatedAtUtc == null)
            .OrderBy(play => play.QualifiedAtUtc)
            .ThenBy(play => play.Id)
            .Take(request.EventBatchSize)
            .ToListAsync(ct);
        foreach (var play in pendingEvents)
            play.AggregatedAtUtc = now;

        await _db.SaveChangesAsync(ct);

        var remainingProjection = await LoadProjectionAsync(trackIds, ct);
        var remainingMismatchCount = CountMismatches(remainingProjection);
        var remainingPendingCount = remainingProjection.Ledger.Values.Sum(row => row.PendingCount);
        var oldestPending = remainingProjection.Ledger.Values
            .Where(row => row.OldestPendingQualifiedAtUtc.HasValue)
            .Select(row => row.OldestPendingQualifiedAtUtc)
            .Min();
        var completedAtUtc = DateTime.UtcNow;
        var lagSeconds = AgeSeconds(completedAtUtc, oldestPending);

        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = "play_reconciliation_repair",
            Admin = requestingAdminId,
            Timestamp = completedAtUtc,
            Details = JsonSerializer.Serialize(new
            {
                status = "completed",
                request.TrackBatchSize,
                request.EventBatchSize,
                candidateTrackCount = candidateTrackIds.Count,
                repairedTrackCount = selectedTrackIds.Length,
                eventsMarkedAggregated = pendingEvents.Count,
                repairedTrackIds = selectedTrackIds,
                remainingMismatchCount,
                remainingPendingCount,
                oldestPendingQualifiedAtUtc = oldestPending,
                aggregationLagSeconds = lagSeconds,
            }, AuditJsonOptions),
        });
        await _db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        if (selectedTrackIds.Length > 0)
            CambrianMetrics.PlayReconciliationRepair.Add(selectedTrackIds.Length);
        if (lagSeconds is double lag)
            CambrianMetrics.PlayAggregationLagSeconds.Record(lag);

        _logger.LogInformation(
            "EVENT: PlayReconciliationRepairCompleted adminId:{AdminId} candidates:{Candidates} repairedTracks:{RepairedTracks} markedEvents:{MarkedEvents} remainingMismatches:{RemainingMismatches} remainingPending:{RemainingPending}",
            requestingAdminId,
            candidateTrackIds.Count,
            selectedTrackIds.Length,
            pendingEvents.Count,
            remainingMismatchCount,
            remainingPendingCount);

        return new PlayReconciliationRepairResult(
            Status: "completed",
            LockAcquired: true,
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: completedAtUtc,
            TrackBatchSize: request.TrackBatchSize,
            EventBatchSize: request.EventBatchSize,
            CandidateTrackCount: candidateTrackIds.Count,
            RepairedTrackCount: selectedTrackIds.Length,
            EventsMarkedAggregated: pendingEvents.Count,
            RepairedTrackIds: selectedTrackIds,
            RemainingMismatchedTrackCount: remainingMismatchCount,
            RemainingPendingAggregationCount: remainingPendingCount,
            OldestPendingQualifiedAtUtc: oldestPending,
            AggregationLagSeconds: lagSeconds);
    }

    public async Task<PlayPipelineHealthDetails> GetHealthDetailsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var eventCount = await _db.QualifiedPlayEvents.AsNoTracking().LongCountAsync(ct);
        var pendingCount = await _db.QualifiedPlayEvents.AsNoTracking()
            .LongCountAsync(play => play.AggregatedAtUtc == null, ct);
        var oldestPending = await _db.QualifiedPlayEvents.AsNoTracking()
            .Where(play => play.AggregatedAtUtc == null)
            .Select(play => (DateTime?)play.QualifiedAtUtc)
            .MinAsync(ct);
        var lagSeconds = AgeSeconds(now, oldestPending);
        var chart = await GetChartFreshnessAsync(now, ct);
        var nonzeroTrendingScoreCount = await _db.Tracks.AsNoTracking()
            .CountAsync(track => track.TrendingScore != 0m, ct);

        if (lagSeconds is double lag)
            CambrianMetrics.PlayAggregationLagSeconds.Record(lag);

        var aggregationStale = lagSeconds > AggregationFreshnessSlaSeconds;
        var status = aggregationStale || chart.StaleWindowCount > 0
            ? "degraded"
            : "ok";

        return new PlayPipelineHealthDetails(
            Status: status,
            QualifiedEventCount: eventCount,
            PendingAggregationCount: pendingCount,
            OldestPendingQualifiedAtUtc: oldestPending,
            AggregationLagSeconds: lagSeconds,
            LatestChartComputedAtUtc: chart.ComputedAtUtc,
            LatestChartDataThroughUtc: chart.DataThroughUtc,
            ChartDataAgeSeconds: chart.DataAgeSeconds,
            StaleChartWindowCount: chart.StaleWindowCount,
            LegacyNonzeroTrendingScoreCount: nonzeroTrendingScoreCount);
    }

    private async Task<PlayReconciliationReport> BuildReportAsync(
        HashSet<Guid>? trackIds,
        int mismatchLimit,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var projection = await LoadProjectionAsync(trackIds, ct);
        var mismatches = BuildMismatches(projection);

        var eventQuery = FilterEvents(trackIds);
        var duplicateIdempotencyKeyGroupCount = await eventQuery
            .GroupBy(play => play.IdempotencyKey)
            .Where(group => group.Count() > 1)
            .LongCountAsync(ct);
        var duplicatePlaybackSessionGroupCount = await eventQuery
            .GroupBy(play => new { play.TrackId, play.PlaybackSessionId })
            .Where(group => group.Count() > 1)
            .LongCountAsync(ct);

        var streamQuery = _db.StreamSessions.AsNoTracking().AsQueryable();
        var trackQuery = _db.Tracks.AsNoTracking().AsQueryable();
        if (trackIds is not null)
        {
            streamQuery = streamQuery.Where(session => trackIds.Contains(session.TrackId));
            trackQuery = trackQuery.Where(track => trackIds.Contains(track.Id));
        }

        var legacySessionCount = await streamQuery
            .LongCountAsync(
                session => session.QualificationStatus == "legacy_unqualified",
                ct);
        var pendingCount = projection.Ledger.Values.Sum(row => row.PendingCount);
        var oldestPending = projection.Ledger.Values
            .Where(row => row.OldestPendingQualifiedAtUtc.HasValue)
            .Select(row => row.OldestPendingQualifiedAtUtc)
            .Min();
        var aggregationLagSeconds = AgeSeconds(now, oldestPending);
        var chart = await GetChartFreshnessAsync(now, ct);
        var nonzeroTrendingScoreCount = await trackQuery
            .CountAsync(track => track.TrendingScore != 0m, ct);

        return new PlayReconciliationReport(
            CheckedAtUtc: now,
            SelectedTrackCount: trackIds?.Count,
            QualifiedEventCount: projection.Ledger.Values.Sum(row => row.QualifiedCount),
            StoredQualifiedPlayCount: projection.Stats.Values.Sum(row => row.QualifiedPlayCount),
            StoredLifetimePlayCount: projection.Stats.Values.Sum(row => row.PlayCount),
            MismatchedTrackCount: mismatches.Count,
            Mismatches: mismatches.Take(mismatchLimit).ToArray(),
            MismatchesTruncated: mismatches.Count > mismatchLimit,
            DuplicateIdempotencyKeyGroupCount: duplicateIdempotencyKeyGroupCount,
            DuplicatePlaybackSessionGroupCount: duplicatePlaybackSessionGroupCount,
            LegacyStreamSessionCount: legacySessionCount,
            HistoricalSessionsWithoutReconstructableQualificationCount: legacySessionCount,
            PendingAggregationCount: pendingCount,
            OldestPendingQualifiedAtUtc: oldestPending,
            AggregationLagSeconds: aggregationLagSeconds,
            LatestChartWeekStartUtc: chart.WeekStartUtc,
            LatestChartComputedAtUtc: chart.ComputedAtUtc,
            LatestChartDataThroughUtc: chart.DataThroughUtc,
            ChartDataAgeSeconds: chart.DataAgeSeconds,
            StaleChartWindowCount: chart.StaleWindowCount,
            LegacyNonzeroTrendingScoreCount: nonzeroTrendingScoreCount);
    }

    private async Task<ProjectionSnapshot> LoadProjectionAsync(
        HashSet<Guid>? trackIds,
        CancellationToken ct)
    {
        var eventQuery = FilterEvents(trackIds);
        var ledgerRows = await eventQuery
            .GroupBy(play => play.TrackId)
            .Select(group => new LedgerRow
            {
                TrackId = group.Key,
                QualifiedCount = group.LongCount(),
                PendingCount = group.LongCount(play => play.AggregatedAtUtc == null),
                LatestQualifiedAtUtc = group.Max(play => (DateTime?)play.QualifiedAtUtc),
                OldestPendingQualifiedAtUtc = group
                    .Where(play => play.AggregatedAtUtc == null)
                    .Min(play => (DateTime?)play.QualifiedAtUtc),
            })
            .ToListAsync(ct);

        var statQuery = _db.TrackStats.AsNoTracking().AsQueryable();
        if (trackIds is not null)
            statQuery = statQuery.Where(stat => trackIds.Contains(stat.TrackId));

        var statRows = await statQuery
            .Select(stat => new StatRow
            {
                TrackId = stat.TrackId,
                LegacyPlayCount = stat.LegacyPlayCount,
                QualifiedPlayCount = stat.QualifiedPlayCount,
                PlayCount = stat.PlayCount,
            })
            .ToListAsync(ct);

        return new ProjectionSnapshot(
            ledgerRows.ToDictionary(row => row.TrackId),
            statRows.ToDictionary(row => row.TrackId));
    }

    private IQueryable<QualifiedPlayEvent> FilterEvents(HashSet<Guid>? trackIds)
    {
        var query = _db.QualifiedPlayEvents.AsNoTracking().AsQueryable();
        return trackIds is null
            ? query
            : query.Where(play => trackIds.Contains(play.TrackId));
    }

    private static List<PlayCountMismatch> BuildMismatches(ProjectionSnapshot projection)
    {
        var mismatches = new List<PlayCountMismatch>();
        foreach (var trackId in projection.Ledger.Keys
                     .Concat(projection.Stats.Keys)
                     .Distinct()
                     .OrderBy(id => id))
        {
            projection.Ledger.TryGetValue(trackId, out var ledger);
            projection.Stats.TryGetValue(trackId, out var stat);
            var legacyCount = stat?.LegacyPlayCount ?? 0L;
            var ledgerCount = ledger?.QualifiedCount ?? 0L;
            var expectedLifetimeCount = checked(legacyCount + ledgerCount);
            var storedQualifiedCount = stat?.QualifiedPlayCount ?? 0L;
            var storedLifetimeCount = stat?.PlayCount ?? 0L;

            if (storedQualifiedCount == ledgerCount
                && storedLifetimeCount == expectedLifetimeCount)
            {
                continue;
            }

            mismatches.Add(new PlayCountMismatch(
                TrackId: trackId,
                LegacyPlayCount: legacyCount,
                LedgerQualifiedPlayCount: ledgerCount,
                StoredQualifiedPlayCount: storedQualifiedCount,
                ExpectedLifetimePlayCount: expectedLifetimeCount,
                StoredLifetimePlayCount: storedLifetimeCount,
                PendingAggregationCount: ledger?.PendingCount ?? 0L,
                LatestQualifiedAtUtc: ledger?.LatestQualifiedAtUtc));
        }

        return mismatches;
    }

    private static IEnumerable<Guid> GetCandidateTrackIds(ProjectionSnapshot projection)
    {
        var mismatchIds = BuildMismatches(projection).Select(row => row.TrackId);
        var pendingIds = projection.Ledger.Values
            .Where(row => row.PendingCount > 0)
            .Select(row => row.TrackId);
        return mismatchIds.Concat(pendingIds).Distinct();
    }

    private static int CountMismatches(ProjectionSnapshot projection)
        => BuildMismatches(projection).Count;

    private async Task<ChartFreshness> GetChartFreshnessAsync(
        DateTime now,
        CancellationToken ct)
    {
        var latestWeek = await _db.WeeklyChartSnapshots.AsNoTracking()
            .Select(row => (DateTime?)row.WeekStartUtc)
            .MaxAsync(ct);
        if (latestWeek is null)
            return new ChartFreshness(null, null, null, null, 1);

        var latestRows = _db.WeeklyChartSnapshots.AsNoTracking()
            .Where(row => row.WeekStartUtc == latestWeek.Value);
        var computedAt = await latestRows
            .Select(row => (DateTime?)row.ComputedAtUtc)
            .MaxAsync(ct);
        var dataThrough = await latestRows
            .Select(row => row.DataThroughUtc)
            .MaxAsync(ct);
        var freshnessAnchor = dataThrough ?? computedAt;
        var dataAgeSeconds = AgeSeconds(now, freshnessAnchor);
        var currentWeekStart = StartOfIsoWeekUtc(now);
        var staleCount = latestWeek.Value != currentWeekStart
                         || freshnessAnchor is null
                         || dataAgeSeconds > ChartFreshnessSlaSeconds
            ? 1
            : 0;

        return new ChartFreshness(
            latestWeek,
            computedAt,
            dataThrough,
            dataAgeSeconds,
            staleCount);
    }

    private async Task<bool> TryAcquireRepairLockAsync(CancellationToken ct)
    {
        if (!IsPostgreSql())
            return true;

        var connection = _db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT pg_try_advisory_xact_lock(@lock_key)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.Value = PostgreSqlAdvisoryLockKey;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync(ct);
        return result is bool acquired && acquired;
    }

    private async Task LockTrackStatsAsync(
        IReadOnlyCollection<Guid> trackIds,
        CancellationToken ct)
    {
        if (!IsPostgreSql() || trackIds.Count == 0)
            return;

        // Use the exact same per-track lock namespace as qualified-play acceptance.
        // Row locks alone do not protect a track whose TrackStats row does not exist yet;
        // the advisory lock closes that insert-vs-repair race across API instances.
        foreach (var trackId in trackIds.OrderBy(id => id))
        {
            var key = $"qualified-play-track:{trackId:D}";
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))",
                ct);
        }

        var connection = _db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
        var parameterNames = new List<string>(trackIds.Count);
        var index = 0;
        foreach (var trackId in trackIds)
        {
            var parameterName = $"track_id_{index++}";
            parameterNames.Add($"@{parameterName}");
            var parameter = command.CreateParameter();
            parameter.ParameterName = parameterName;
            parameter.Value = trackId;
            command.Parameters.Add(parameter);
        }

        command.CommandText = $"SELECT \"TrackId\" FROM \"TrackStats\" WHERE \"TrackId\" IN ({string.Join(",", parameterNames)}) FOR UPDATE";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // Reading all rows ensures every matching projection row is locked.
        }
    }

    private bool IsPostgreSql()
        => _db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    private static HashSet<Guid>? NormalizeTrackIds(
        IReadOnlyCollection<Guid>? trackIds,
        int maximumCount,
        string parameterName)
    {
        if (trackIds is null)
            return null;
        if (trackIds.Count > maximumCount)
            throw new ArgumentOutOfRangeException(parameterName, $"At most {maximumCount} track IDs are allowed.");
        if (trackIds.Any(id => id == Guid.Empty))
            throw new ArgumentException("Track IDs must be non-empty UUIDs.", parameterName);
        return trackIds.ToHashSet();
    }

    private static void ValidateRange(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName, value, $"Value must be between {minimum} and {maximum}.");
    }

    private static void ValidateAdminId(string requestingAdminId)
    {
        if (string.IsNullOrWhiteSpace(requestingAdminId))
            throw new ArgumentException("A requesting admin ID is required.", nameof(requestingAdminId));
    }

    private static double? AgeSeconds(DateTime now, DateTime? timestamp)
        => timestamp is null
            ? null
            : Math.Max(0, (now - timestamp.Value).TotalSeconds);

    private static DateTime StartOfIsoWeekUtc(DateTime utc)
    {
        var date = DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
        var daysSinceMonday = (7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return date.AddDays(-daysSinceMonday);
    }

    private sealed record ProjectionSnapshot(
        IReadOnlyDictionary<Guid, LedgerRow> Ledger,
        IReadOnlyDictionary<Guid, StatRow> Stats);

    private sealed class LedgerRow
    {
        public Guid TrackId { get; init; }
        public long QualifiedCount { get; init; }
        public long PendingCount { get; init; }
        public DateTime? LatestQualifiedAtUtc { get; init; }
        public DateTime? OldestPendingQualifiedAtUtc { get; init; }
    }

    private sealed class StatRow
    {
        public Guid TrackId { get; init; }
        public long LegacyPlayCount { get; init; }
        public long QualifiedPlayCount { get; init; }
        public long PlayCount { get; init; }
    }

    private sealed record ChartFreshness(
        DateTime? WeekStartUtc,
        DateTime? ComputedAtUtc,
        DateTime? DataThroughUtc,
        double? DataAgeSeconds,
        int StaleWindowCount);
}
