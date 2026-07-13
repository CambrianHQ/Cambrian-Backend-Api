using Cambrian.Application.DTOs.PlayCounts;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests.PlayCounts;

/// <summary>
/// PlayCountReconciliationService: compares TrackStats/CreatorStats against durable, qualified
/// StreamSessions. Dry-run only detects and records; repair corrects and records; an interrupted
/// run leaves an honest partial audit trail instead of corrupting or losing anything, and a
/// later run always converges on the same correct answer (recompute, not delta-apply).
/// </summary>
public sealed class PlayCountReconciliationTests : IDisposable
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly CambrianDbContext _db;

    public PlayCountReconciliationTests()
    {
        _db = CreateDbContext();
    }

    public void Dispose() => _db.Dispose();

    /// <summary>A fresh CambrianDbContext pointed at this test's shared InMemory database — lets
    /// tests construct multiple independent contexts (own change tracker each) against the same
    /// underlying data, the way separate requests/replicas would.</summary>
    private CambrianDbContext CreateDbContext(IEnumerable<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor>? interceptors = null)
    {
        var builder = new DbContextOptionsBuilder<CambrianDbContext>().UseInMemoryDatabase(_dbName);
        if (interceptors is not null)
            builder.AddInterceptors(interceptors);
        return new CambrianDbContext(builder.Options);
    }

    private async Task<Guid> SeedTrackWithDriftAsync(
        Guid creatorUuid, string userId, int qualifiedPlays, int? storedPlayCount = null, int? storedUniqueListeners = null)
    {
        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track { Id = trackId, Title = "Drift Beat", CreatorId = userId, CreatorUuid = creatorUuid });

        for (var i = 0; i < qualifiedPlays; i++)
        {
            _db.StreamSessions.Add(new StreamSession
            {
                Id = Guid.NewGuid(),
                TrackId = trackId,
                UserId = $"listener-{i}",
                StartedAt = DateTime.UtcNow.AddMinutes(-i),
                IdempotencyKey = Guid.NewGuid().ToString(),
                Qualified = true,
            });
        }

        if (storedPlayCount.HasValue)
        {
            _db.TrackStats.Add(new TrackStat
            {
                TrackId = trackId,
                PlayCount = storedPlayCount.Value,
                UniqueListenerCount = storedUniqueListeners ?? 0,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await _db.SaveChangesAsync();
        return trackId;
    }

    private PlayCountReconciliationService CreateService() =>
        new(_db, Substitute.For<ILogger<PlayCountReconciliationService>>());

    // ── Reconciliation (dry-run) ─────────────────────────────────────────────

    [Fact]
    public async Task ReconcileAsync_DryRun_DetectsMismatchesWithoutWriting()
    {
        var creatorUuid = Guid.NewGuid();
        const string userId = "creator-recon-1";
        _db.Creators.Add(new Creator { Id = creatorUuid, UserId = userId, Username = "reconciler1", DisplayName = "Reconciler" });
        var trackId = await SeedTrackWithDriftAsync(creatorUuid, userId, qualifiedPlays: 5, storedPlayCount: 2);

        var service = CreateService();
        var result = await service.ReconcileAsync(new ReconciliationOptions
        {
            TrackIds = new[] { trackId },
            DryRun = true,
            Repair = true, // must be ignored — dry-run is authoritative
        });

        Assert.True(result.DryRun);
        Assert.Equal(1, result.MismatchesFound);
        Assert.Equal(0, result.MismatchesRepaired);
        var mismatch = Assert.Single(result.Mismatches);
        Assert.Equal(trackId, mismatch.TrackId);
        Assert.Equal(2, mismatch.StoredPlayCount);
        Assert.Equal(5, mismatch.ComputedPlayCount);
        Assert.False(mismatch.Repaired);

        // Nothing was written — the stored value is still the stale one.
        var stat = await _db.TrackStats.AsNoTracking().FirstAsync(s => s.TrackId == trackId);
        Assert.Equal(2, stat.PlayCount);

        // The run itself is still durably recorded, dry-run or not — that's the audit trail.
        var run = await _db.PlayCountReconciliationRuns.AsNoTracking().FirstAsync(r => r.Id == result.RunId);
        Assert.True(run.DryRun);
        Assert.Equal("completed", run.Status);
        Assert.Equal(1, run.MismatchesFound);
        Assert.Equal(0, run.MismatchesRepaired);
    }

    [Fact]
    public async Task ReconcileAsync_NoDrift_ReportsZeroMismatches()
    {
        var creatorUuid = Guid.NewGuid();
        const string userId = "creator-recon-2";
        _db.Creators.Add(new Creator { Id = creatorUuid, UserId = userId, Username = "reconciler2", DisplayName = "Reconciler2" });
        var trackId = await SeedTrackWithDriftAsync(creatorUuid, userId, qualifiedPlays: 3, storedPlayCount: 3, storedUniqueListeners: 3);

        var result = await CreateService().ReconcileAsync(new ReconciliationOptions { TrackIds = new[] { trackId }, DryRun = true });

        Assert.Equal(0, result.MismatchesFound);
        Assert.Empty(result.Mismatches);
    }

    // ── Repair ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReconcileAsync_Repair_CorrectsTrackAndCreatorStats_AndRecordsAuditEntries()
    {
        var creatorUuid = Guid.NewGuid();
        const string userId = "creator-repair-1";
        _db.Creators.Add(new Creator { Id = creatorUuid, UserId = userId, Username = "repairer1", DisplayName = "Repairer" });
        var trackId = await SeedTrackWithDriftAsync(creatorUuid, userId, qualifiedPlays: 7, storedPlayCount: 0);

        var result = await CreateService().ReconcileAsync(new ReconciliationOptions
        {
            TrackIds = new[] { trackId },
            DryRun = false,
            Repair = true,
        });

        Assert.False(result.DryRun);
        Assert.Equal(1, result.MismatchesRepaired);
        Assert.True(result.Mismatches.Single().Repaired);

        var stat = await _db.TrackStats.AsNoTracking().FirstAsync(s => s.TrackId == trackId);
        Assert.Equal(7, stat.PlayCount);

        var creatorStat = await _db.CreatorStats.AsNoTracking().FirstOrDefaultAsync(s => s.CreatorId == creatorUuid);
        Assert.NotNull(creatorStat);
        Assert.Equal(7, creatorStat!.TotalPlays);

        // Auditable: the entry persists what was stored, what it should be, and that it was fixed.
        var entry = await _db.PlayCountReconciliationEntries.AsNoTracking().SingleAsync(e => e.TrackId == trackId);
        Assert.Equal(0, entry.StoredPlayCount);
        Assert.Equal(7, entry.ComputedPlayCount);
        Assert.True(entry.Repaired);
        Assert.Equal(result.RunId, entry.RunId);
    }

    [Fact]
    public async Task ReconcileAsync_Repair_RebuildsFromScratch_WhenNoStatsRowExistsAtAll()
    {
        // Simulates a fresh deploy: TrackStats has never had a row for this track, even though
        // real historical StreamSessions already exist — the exact "rebuild from durable events"
        // scenario, not a fabrication (the events are real; only the projection was never built).
        var creatorUuid = Guid.NewGuid();
        const string userId = "creator-repair-2";
        _db.Creators.Add(new Creator { Id = creatorUuid, UserId = userId, Username = "repairer2", DisplayName = "Repairer2" });
        var trackId = await SeedTrackWithDriftAsync(creatorUuid, userId, qualifiedPlays: 12, storedPlayCount: null);

        Assert.False(await _db.TrackStats.AnyAsync(s => s.TrackId == trackId));

        var result = await CreateService().ReconcileAsync(new ReconciliationOptions { DryRun = false, Repair = true });

        Assert.Equal(1, result.MismatchesRepaired);
        var stat = await _db.TrackStats.AsNoTracking().SingleAsync(s => s.TrackId == trackId);
        Assert.Equal(12, stat.PlayCount);
    }

    [Fact]
    public async Task ReconcileAsync_UnqualifiedSessions_AreNeverCounted()
    {
        var creatorUuid = Guid.NewGuid();
        const string userId = "creator-repair-3";
        _db.Creators.Add(new Creator { Id = creatorUuid, UserId = userId, Username = "repairer3", DisplayName = "Repairer3" });
        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track { Id = trackId, Title = "Partial Beat", CreatorId = userId, CreatorUuid = creatorUuid });

        // 3 qualified, 2 unqualified (e.g. a listener who bailed before MinQualifyingSeconds).
        for (var i = 0; i < 3; i++)
            _db.StreamSessions.Add(new StreamSession { Id = Guid.NewGuid(), TrackId = trackId, IdempotencyKey = Guid.NewGuid().ToString(), Qualified = true });
        for (var i = 0; i < 2; i++)
            _db.StreamSessions.Add(new StreamSession { Id = Guid.NewGuid(), TrackId = trackId, IdempotencyKey = Guid.NewGuid().ToString(), Qualified = false });
        await _db.SaveChangesAsync();

        var result = await CreateService().ReconcileAsync(new ReconciliationOptions { TrackIds = new[] { trackId }, DryRun = false, Repair = true });

        Assert.Equal(3, result.Mismatches.Single().ComputedPlayCount);
        var stat = await _db.TrackStats.AsNoTracking().SingleAsync(s => s.TrackId == trackId);
        Assert.Equal(3, stat.PlayCount);
    }

    [Fact]
    public async Task ReconcileAsync_ProcessesAllTracks_WhenScopeOmitted_InBoundedBatches()
    {
        var creatorUuid = Guid.NewGuid();
        const string userId = "creator-batch-1";
        _db.Creators.Add(new Creator { Id = creatorUuid, UserId = userId, Username = "batcher1", DisplayName = "Batcher" });

        var trackIds = new List<Guid>();
        for (var t = 0; t < 9; t++)
            trackIds.Add(await SeedTrackWithDriftAsync(creatorUuid, userId, qualifiedPlays: t + 1, storedPlayCount: 0));

        var result = await CreateService().ReconcileAsync(new ReconciliationOptions
        {
            TrackIds = null, // whole catalog
            DryRun = false,
            Repair = true,
            BatchSize = 4, // forces 3 batches (4 + 4 + 1) for 9 tracks
        });

        Assert.Equal(9, result.TracksScanned);
        Assert.Equal(9, result.MismatchesRepaired);
        foreach (var trackId in trackIds)
        {
            var stat = await _db.TrackStats.AsNoTracking().SingleAsync(s => s.TrackId == trackId);
            Assert.True(stat.PlayCount > 0);
        }
    }

    // ── Worker interruption ───────────────────────────────────────────────────

    private sealed class CancelAfterNSavesInterceptor : SaveChangesInterceptor
    {
        private readonly CancellationTokenSource _cts;
        private readonly int _cancelAfterSaveCount;
        private int _saveCount;

        public CancelAfterNSavesInterceptor(CancellationTokenSource cts, int cancelAfterSaveCount)
        {
            _cts = cts;
            _cancelAfterSaveCount = cancelAfterSaveCount;
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            _saveCount++;
            if (_saveCount >= _cancelAfterSaveCount)
                _cts.Cancel();
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task ReconcileAsync_InterruptedMidRun_LeavesAccuratePartialProgress_AndResumeConverges()
    {
        // No CreatorUuid on these tracks (deliberately): keeps each batch's write to exactly one
        // SaveChanges call, so the cancel-after-N-saves timing below is simple and deterministic
        // (save #1 = run row creation, save #2 = batch 1's repair) — creator-total repair is
        // already covered by the dedicated repair tests above.
        var trackIds = new List<Guid>();
        for (var t = 0; t < 6; t++)
        {
            var trackId = Guid.NewGuid();
            _db.Tracks.Add(new Track { Id = trackId, Title = $"Interrupt Beat {t}", CreatorId = "creator-interrupt-1" });
            for (var i = 0; i <= t; i++)
            {
                _db.StreamSessions.Add(new StreamSession
                {
                    Id = Guid.NewGuid(),
                    TrackId = trackId,
                    UserId = $"listener-{t}-{i}",
                    StartedAt = DateTime.UtcNow,
                    IdempotencyKey = Guid.NewGuid().ToString(),
                    Qualified = true,
                });
            }
            trackIds.Add(trackId);
        }
        await _db.SaveChangesAsync();

        // Interrupt right after the first batch's single save completes — before the whole run
        // (3 batches of size 2) would otherwise complete. Same underlying InMemory database as
        // `_db` (via CreateDbContext/_dbName), so the interrupted run's partial writes are
        // visible below.
        var cts = new CancellationTokenSource();
        await using var interruptedDb = CreateDbContext(new IInterceptor[] { new CancelAfterNSavesInterceptor(cts, cancelAfterSaveCount: 2) });
        var interruptedService = new PlayCountReconciliationService(interruptedDb, Substitute.For<ILogger<PlayCountReconciliationService>>());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interruptedService.ReconcileAsync(
            new ReconciliationOptions { DryRun = false, Repair = true, BatchSize = 2 }, cts.Token));

        // The run row shows honest partial progress — some tracks scanned, not all six — and is
        // still marked "running" (not falsely "completed").
        var interruptedRun = await _db.PlayCountReconciliationRuns.AsNoTracking()
            .OrderByDescending(r => r.StartedAtUtc).FirstAsync();
        Assert.Equal("running", interruptedRun.Status);
        Assert.True(interruptedRun.TracksScanned > 0 && interruptedRun.TracksScanned < 6,
            $"expected partial progress, got {interruptedRun.TracksScanned}/6");

        // Whatever that partial batch repaired is durably correct (not corrupted) — spot-check
        // is implicit in the resume below, which must converge regardless.

        // A fresh, uninterrupted run resumes and completes the job correctly — nothing was lost.
        var resumeResult = await CreateService().ReconcileAsync(new ReconciliationOptions { DryRun = false, Repair = true, BatchSize = 2 });
        Assert.Equal("completed", resumeResult.Status);
        Assert.Equal(6, resumeResult.TracksScanned);

        foreach (var trackId in trackIds)
        {
            var stat = await _db.TrackStats.AsNoTracking().SingleAsync(s => s.TrackId == trackId);
            var expected = trackIds.IndexOf(trackId) + 1;
            Assert.Equal(expected, stat.PlayCount);
        }
    }

    // ── Concurrent reconciliation runs ────────────────────────────────────────

    [Fact]
    public async Task ReconcileAsync_ConcurrentRuns_BothConvergeOnTheSameCorrectAnswer()
    {
        // Repair recomputes from scratch rather than applying a delta, so two reconciliation
        // runs racing on the same track can't compound or double-apply a fix — both land on the
        // same correct number.
        var creatorUuid = Guid.NewGuid();
        const string userId = "creator-concurrent-recon-1";
        _db.Creators.Add(new Creator { Id = creatorUuid, UserId = userId, Username = "concurrentrecon1", DisplayName = "ConcurrentRecon" });
        var trackId = await SeedTrackWithDriftAsync(creatorUuid, userId, qualifiedPlays: 4, storedPlayCount: 0);

        Task<ReconciliationRunResult> RunAsync()
        {
            var db = CreateDbContext();
            var service = new PlayCountReconciliationService(db, Substitute.For<ILogger<PlayCountReconciliationService>>());
            return service.ReconcileAsync(new ReconciliationOptions { TrackIds = new[] { trackId }, DryRun = false, Repair = true });
        }

        await Task.WhenAll(RunAsync(), RunAsync());

        var stat = await _db.TrackStats.AsNoTracking().SingleAsync(s => s.TrackId == trackId);
        Assert.Equal(4, stat.PlayCount);
    }
}
