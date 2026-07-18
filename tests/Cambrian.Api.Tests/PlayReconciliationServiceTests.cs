using Cambrian.Application.DTOs.Admin;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cambrian.Api.Tests;

public sealed class PlayReconciliationServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private CambrianDbContext _db = null!;
    private PlayReconciliationService _service = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new CambrianDbContext(options);
        await _db.Database.EnsureCreatedAsync();
        _service = new PlayReconciliationService(
            _db,
            NullLogger<PlayReconciliationService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task DryRun_reports_drift_and_writes_an_admin_audit_without_mutating_counts()
    {
        var seeded = await SeedTrackWithPlayAsync(
            legacyPlayCount: 7,
            storedQualifiedPlayCount: 0,
            storedLifetimePlayCount: 7,
            aggregatedAtUtc: null);

        var report = await _service.InspectAsync(
            new PlayReconciliationRequest { TrackIds = [seeded.TrackId] },
            "admin-user-id");

        var stat = await _db.TrackStats.AsNoTracking().SingleAsync(row => row.TrackId == seeded.TrackId);
        Assert.Equal(1, report.MismatchedTrackCount);
        Assert.Equal(1, report.QualifiedEventCount);
        Assert.Equal(1, report.PendingAggregationCount);
        Assert.Equal(1, report.HistoricalSessionsWithoutReconstructableQualificationCount);
        Assert.Equal(7, stat.PlayCount);
        Assert.Equal(0, stat.QualifiedPlayCount);

        var audit = await _db.AuditLogs.AsNoTracking().SingleAsync();
        Assert.Equal("play_reconciliation_dry_run", audit.Action);
        Assert.Equal("admin-user-id", audit.Admin);
        Assert.Contains("mismatchedTrackCount", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repair_rebuilds_from_existing_events_preserves_legacy_baseline_and_is_idempotent()
    {
        var seeded = await SeedTrackWithPlayAsync(
            legacyPlayCount: 7,
            storedQualifiedPlayCount: 0,
            storedLifetimePlayCount: 7,
            aggregatedAtUtc: null);
        var eventCountBefore = await _db.QualifiedPlayEvents.CountAsync();

        var first = await _service.RepairAsync(
            new PlayReconciliationRepairRequest
            {
                TrackIds = [seeded.TrackId],
                TrackBatchSize = 10,
                EventBatchSize = 10,
            },
            "admin-user-id");

        _db.ChangeTracker.Clear();
        var stat = await _db.TrackStats.SingleAsync(row => row.TrackId == seeded.TrackId);
        var play = await _db.QualifiedPlayEvents.SingleAsync(row => row.Id == seeded.EventId);
        Assert.True(first.LockAcquired);
        Assert.Equal("completed", first.Status);
        Assert.Equal(1, first.RepairedTrackCount);
        Assert.Equal(1, first.EventsMarkedAggregated);
        Assert.Equal(7, stat.LegacyPlayCount);
        Assert.Equal(1, stat.QualifiedPlayCount);
        Assert.Equal(8, stat.PlayCount);
        Assert.NotNull(stat.ReconciledAtUtc);
        Assert.NotNull(play.AggregatedAtUtc);
        Assert.Equal(eventCountBefore, await _db.QualifiedPlayEvents.CountAsync());

        var second = await _service.RepairAsync(
            new PlayReconciliationRepairRequest
            {
                TrackIds = [seeded.TrackId],
                TrackBatchSize = 10,
                EventBatchSize = 10,
            },
            "admin-user-id");

        Assert.Equal(0, second.CandidateTrackCount);
        Assert.Equal(0, second.RepairedTrackCount);
        Assert.Equal(eventCountBefore, await _db.QualifiedPlayEvents.CountAsync());
        Assert.Equal(2, await _db.AuditLogs.CountAsync(row => row.Action == "play_reconciliation_repair"));
    }

    [Fact]
    public async Task Repair_honors_track_batch_bound_and_leaves_remaining_mismatch_observable()
    {
        var first = await SeedTrackWithPlayAsync(0, 0, 0, DateTime.UtcNow);
        var second = await SeedTrackWithPlayAsync(0, 0, 0, DateTime.UtcNow);

        var result = await _service.RepairAsync(
            new PlayReconciliationRepairRequest
            {
                TrackIds = [first.TrackId, second.TrackId],
                TrackBatchSize = 1,
                EventBatchSize = 10,
            },
            "admin-user-id");

        Assert.Equal(2, result.CandidateTrackCount);
        Assert.Equal(1, result.RepairedTrackCount);
        Assert.Equal(1, result.RemainingMismatchedTrackCount);
        Assert.Single(result.RepairedTrackIds);
        Assert.Equal(2, await _db.QualifiedPlayEvents.CountAsync());
    }

    [Fact]
    public async Task Repair_honors_event_marker_batch_without_losing_ledger_count()
    {
        var seeded = await SeedTrackWithPlayAsync(0, 0, 0, null);
        await AddQualifiedPlayAsync(seeded.TrackId, aggregatedAtUtc: null);

        var result = await _service.RepairAsync(
            new PlayReconciliationRepairRequest
            {
                TrackIds = [seeded.TrackId],
                TrackBatchSize = 1,
                EventBatchSize = 1,
            },
            "admin-user-id");

        _db.ChangeTracker.Clear();
        var stat = await _db.TrackStats.SingleAsync(row => row.TrackId == seeded.TrackId);
        Assert.Equal(1, result.EventsMarkedAggregated);
        Assert.Equal(1, result.RemainingPendingAggregationCount);
        Assert.Equal(2, stat.QualifiedPlayCount);
        Assert.Equal(2, stat.PlayCount);
        Assert.Equal(2, await _db.QualifiedPlayEvents.CountAsync());
        Assert.Equal(1, await _db.QualifiedPlayEvents.CountAsync(row => row.AggregatedAtUtc == null));
    }

    private async Task<(Guid TrackId, Guid EventId)> SeedTrackWithPlayAsync(
        long legacyPlayCount,
        long storedQualifiedPlayCount,
        long storedLifetimePlayCount,
        DateTime? aggregatedAtUtc)
    {
        var userId = $"creator-{Guid.NewGuid():N}";
        _db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = userId,
            NormalizedUserName = userId.ToUpperInvariant(),
            Email = $"{userId}@example.test",
            NormalizedEmail = $"{userId}@example.test".ToUpperInvariant(),
        });

        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track
        {
            Id = trackId,
            CambrianTrackId = $"CAMB-TRK-{trackId:N}"[..17].ToUpperInvariant(),
            Title = "Reconciliation test track",
            CreatorId = userId,
            Visibility = "public",
            Status = "available",
        });

        var sessionId = Guid.NewGuid();
        _db.StreamSessions.Add(new StreamSession
        {
            Id = sessionId,
            TrackId = trackId,
            UserId = userId,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            StoppedAt = DateTime.UtcNow.AddMinutes(-1),
            QualificationStatus = "legacy_unqualified",
        });

        _db.TrackStats.Add(new TrackStat
        {
            TrackId = trackId,
            LegacyPlayCount = legacyPlayCount,
            QualifiedPlayCount = storedQualifiedPlayCount,
            PlayCount = storedLifetimePlayCount,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
        });

        var eventId = Guid.NewGuid();
        _db.QualifiedPlayEvents.Add(new QualifiedPlayEvent
        {
            Id = eventId,
            IdempotencyKey = $"play-{eventId:N}",
            TrackId = trackId,
            CreatorId = userId,
            ListenerUserId = null,
            ListenerKeyHash = $"listener-{eventId:N}",
            AnonymousSessionHash = $"anon-{eventId:N}",
            PlaybackSessionId = sessionId,
            QualifiedAtUtc = DateTime.UtcNow.AddSeconds(-30),
            QualificationBasis = "active_playback_threshold",
            ActivePlaybackSeconds = 30,
            ThresholdSeconds = 30,
            CreatedAtUtc = DateTime.UtcNow.AddSeconds(-30),
            AggregatedAtUtc = aggregatedAtUtc,
        });

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return (trackId, eventId);
    }

    private async Task<Guid> AddQualifiedPlayAsync(Guid trackId, DateTime? aggregatedAtUtc)
    {
        var track = await _db.Tracks.AsNoTracking().SingleAsync(row => row.Id == trackId);
        var sessionId = Guid.NewGuid();
        _db.StreamSessions.Add(new StreamSession
        {
            Id = sessionId,
            TrackId = trackId,
            UserId = track.CreatorId,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            StoppedAt = DateTime.UtcNow.AddMinutes(-1),
            QualificationStatus = "qualified",
        });

        var eventId = Guid.NewGuid();
        _db.QualifiedPlayEvents.Add(new QualifiedPlayEvent
        {
            Id = eventId,
            IdempotencyKey = $"play-{eventId:N}",
            TrackId = trackId,
            CreatorId = track.CreatorId,
            ListenerKeyHash = $"listener-{eventId:N}",
            AnonymousSessionHash = $"anon-{eventId:N}",
            PlaybackSessionId = sessionId,
            QualifiedAtUtc = DateTime.UtcNow.AddSeconds(-20),
            QualificationBasis = "active_playback_threshold",
            ActivePlaybackSeconds = 30,
            ThresholdSeconds = 30,
            CreatedAtUtc = DateTime.UtcNow.AddSeconds(-20),
            AggregatedAtUtc = aggregatedAtUtc,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return eventId;
    }
}
