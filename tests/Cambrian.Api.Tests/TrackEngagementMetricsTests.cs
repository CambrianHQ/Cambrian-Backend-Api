using Cambrian.Application.DTOs.PlayCounts;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Cambrian.Persistence.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Regression: public track + creator DTOs must surface REAL engagement counts
/// (plays from StreamSessions, sales from completed Purchases, followers from
/// CreatorFollows) — not zeros or fabricated values. Guards the wiring added so
/// the frontend "play count on songs" and creator stats reflect live data.
///
/// Plays now read the TrackStats/CreatorStats projection (IPlayCountService), not a live
/// COUNT over StreamSessions — see CLAUDE.md's play-count rebuild notes. These tests seed
/// StreamSessions directly (bypassing StreamRepository's normal write path, which keeps the
/// projection in sync automatically) to represent "durable events that already existed", then
/// run a real repair reconciliation to populate the projection from them — exactly the rebuild
/// path a fresh deploy or a drift repair uses, not a test-only shortcut.
/// </summary>
public sealed class TrackEngagementMetricsTests : IDisposable
{
    private readonly CambrianDbContext _db;
    private readonly IPlayCountService _playCounts;
    private readonly IPlayCountReconciliationService _reconciliation;

    public TrackEngagementMetricsTests()
    {
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new CambrianDbContext(options);
        _playCounts = new PlayCountService(_db, new MemoryCache(new MemoryCacheOptions()), Substitute.For<ILogger<PlayCountService>>());
        _reconciliation = new PlayCountReconciliationService(_db, Substitute.For<ILogger<PlayCountReconciliationService>>());
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Rebuilds TrackStats/CreatorStats from whatever StreamSessions exist right now.</summary>
    private Task RebuildProjectionAsync() =>
        _reconciliation.ReconcileAsync(new ReconciliationOptions { DryRun = false, Repair = true });

    private static StreamSession QualifiedSession(Guid trackId) => new()
    {
        Id = Guid.NewGuid(),
        TrackId = trackId,
        IdempotencyKey = Guid.NewGuid().ToString(),
        Qualified = true,
    };

    [Fact]
    public async Task GetTrackStatsAsync_CountsPlays_AndOnlyCompletedSales()
    {
        var trackA = Guid.NewGuid();
        var trackB = Guid.NewGuid();
        var trackC = Guid.NewGuid(); // no activity at all

        _db.Tracks.AddRange(
            new Track { Id = trackA, Title = "A", CreatorId = "c1" },
            new Track { Id = trackB, Title = "B", CreatorId = "c1" },
            new Track { Id = trackC, Title = "C", CreatorId = "c1" });

        // Plays: 3 for A, 1 for B
        for (var i = 0; i < 3; i++)
            _db.StreamSessions.Add(QualifiedSession(trackA));
        _db.StreamSessions.Add(QualifiedSession(trackB));

        // Sales: 2 completed + 1 pending for A (pending must NOT count); 0 for B
        _db.Purchases.AddRange(
            new Purchase { Id = Guid.NewGuid(), TrackId = trackA, BuyerId = "b1", Status = "completed" },
            new Purchase { Id = Guid.NewGuid(), TrackId = trackA, BuyerId = "b2", Status = "completed" },
            new Purchase { Id = Guid.NewGuid(), TrackId = trackA, BuyerId = "b3", Status = "pending" });

        // trackA: issued authorship record; trackB: pending only (must not surface).
        var issuedRecordId = Guid.NewGuid();
        _db.AuthorshipRecords.AddRange(
            new AuthorshipRecord { Id = issuedRecordId, TrackId = trackA, CreatorId = "c1", Status = "issued", IssuedAt = DateTime.UtcNow },
            new AuthorshipRecord { Id = Guid.NewGuid(), TrackId = trackB, CreatorId = "c1", Status = "pending_payment" });

        await _db.SaveChangesAsync();
        await RebuildProjectionAsync();

        var repo = new TrackRepository(_db, _playCounts);
        var stats = await repo.GetTrackStatsAsync(new[] { trackA, trackB, trackC });

        Assert.Equal(3, stats[trackA].Plays);
        Assert.Equal(2, stats[trackA].Sales);
        Assert.Equal(issuedRecordId.ToString(), stats[trackA].AuthorshipRecordId);
        Assert.Equal(1, stats[trackB].Plays);
        Assert.Equal(0, stats[trackB].Sales);
        Assert.Null(stats[trackB].AuthorshipRecordId); // pending record must not count
        // A track with no activity is still present, zeroed.
        Assert.Equal(0, stats[trackC].Plays);
        Assert.Equal(0, stats[trackC].Sales);
        Assert.Null(stats[trackC].AuthorshipRecordId);
    }

    [Fact]
    public async Task GetTrackStatsAsync_EmptyInput_ReturnsEmpty()
    {
        var repo = new TrackRepository(_db, _playCounts);
        var stats = await repo.GetTrackStatsAsync(Array.Empty<Guid>());
        Assert.Empty(stats);
    }

    [Fact]
    public async Task GetTracksByCreatorIdAsync_PopulatesPlaysAndSales_FromStreamSessionsAndPurchases()
    {
        // Regression: the public creator profile (/creator/tracks/{slug}) showed
        // zero plays even when the catalog showed real counts — GetTracksByCreatorIdAsync
        // projected the TrackResponse without Plays/Sales, so both defaulted to 0.
        var creatorUuid = Guid.NewGuid();
        const string userId = "u-store-1";
        var trackA = Guid.NewGuid();
        var trackB = Guid.NewGuid();

        _db.Creators.Add(new Creator { Id = creatorUuid, UserId = userId, Username = "storestar", DisplayName = "Store Star" });
        _db.CreatorProfiles.Add(new CreatorProfile { Id = Guid.NewGuid(), UserId = userId, Slug = "storestar" });
        _db.Tracks.AddRange(
            new Track { Id = trackA, Title = "A", CreatorId = userId, CreatorUuid = creatorUuid, Visibility = "public", Status = "available" },
            new Track { Id = trackB, Title = "B", CreatorId = userId, CreatorUuid = creatorUuid, Visibility = "public", Status = "available" });

        // trackA: 5 plays + 2 completed sales (+1 pending, which must NOT count); trackB: none
        for (var i = 0; i < 5; i++)
            _db.StreamSessions.Add(QualifiedSession(trackA));
        _db.Purchases.AddRange(
            new Purchase { Id = Guid.NewGuid(), TrackId = trackA, BuyerId = "b1", Status = "completed" },
            new Purchase { Id = Guid.NewGuid(), TrackId = trackA, BuyerId = "b2", Status = "completed" },
            new Purchase { Id = Guid.NewGuid(), TrackId = trackA, BuyerId = "b3", Status = "pending" });

        // trackA has an ISSUED authorship record; trackB only a pending one (must NOT surface).
        var issuedRecordId = Guid.NewGuid();
        _db.AuthorshipRecords.AddRange(
            new AuthorshipRecord { Id = issuedRecordId, TrackId = trackA, CreatorId = userId, Status = "issued", IssuedAt = DateTime.UtcNow },
            new AuthorshipRecord { Id = Guid.NewGuid(), TrackId = trackB, CreatorId = userId, Status = "pending_payment" });

        await _db.SaveChangesAsync();
        await RebuildProjectionAsync();

        var repo = new CreatorIdentityRepository(_db, _playCounts, Substitute.For<ILogger<CreatorIdentityRepository>>());
        var tracks = await repo.GetTracksByCreatorIdAsync(creatorUuid, 1, 50);

        var a = tracks.Single(t => t.Id == trackA.ToString());
        var b = tracks.Single(t => t.Id == trackB.ToString());
        Assert.Equal(5, a.Plays);
        Assert.Equal(2, a.Sales);
        Assert.Equal(issuedRecordId.ToString(), a.AuthorshipRecordId);
        Assert.Equal(0, b.Plays);
        Assert.Equal(0, b.Sales);
        Assert.Null(b.AuthorshipRecordId); // pending record must not count
    }

    [Fact]
    public async Task CreatorProfileStats_IncludeTotalPlaysAndFollowerCount()
    {
        var creatorUuid = Guid.NewGuid();
        const string userId = "u-metrics-1";
        var trackId = Guid.NewGuid();

        _db.Creators.Add(new Creator
        {
            Id = creatorUuid,
            UserId = userId,
            Username = "metricstar",
            DisplayName = "Metric Star",
        });
        _db.CreatorProfiles.Add(new CreatorProfile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Slug = "metricstar",
        });
        _db.Tracks.Add(new Track { Id = trackId, Title = "T", CreatorId = userId, CreatorUuid = creatorUuid });

        // 4 plays
        for (var i = 0; i < 4; i++)
            _db.StreamSessions.Add(QualifiedSession(trackId));
        // 2 completed sales (downloads)
        _db.Purchases.AddRange(
            new Purchase { Id = Guid.NewGuid(), TrackId = trackId, BuyerId = "b1", Status = "completed" },
            new Purchase { Id = Guid.NewGuid(), TrackId = trackId, BuyerId = "b2", Status = "completed" });
        // 3 followers
        for (var i = 0; i < 3; i++)
            _db.CreatorFollows.Add(new CreatorFollow { Id = Guid.NewGuid(), CreatorId = creatorUuid, FollowerId = $"f{i}" });

        await _db.SaveChangesAsync();
        await RebuildProjectionAsync();

        var repo = new CreatorProfileRepository(_db, _playCounts);
        var profile = await repo.GetBySlugAsync("metricstar");

        Assert.NotNull(profile);
        Assert.Equal(4, profile!.Stats.TotalPlays);
        Assert.Equal(2, profile.Stats.TotalDownloads);
        Assert.Equal(3, profile.Stats.FollowerCount);
    }
}
