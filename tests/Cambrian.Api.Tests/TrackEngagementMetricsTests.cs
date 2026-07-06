using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Regression: public track + creator DTOs must surface REAL engagement counts
/// (plays from StreamSessions, sales from completed Purchases, followers from
/// CreatorFollows) — not zeros or fabricated values. Guards the wiring added so
/// the frontend "play count on songs" and creator stats reflect live data.
/// </summary>
public sealed class TrackEngagementMetricsTests : IDisposable
{
    private readonly CambrianDbContext _db;

    public TrackEngagementMetricsTests()
    {
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new CambrianDbContext(options);
    }

    public void Dispose() => _db.Dispose();

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
            _db.StreamSessions.Add(new StreamSession { Id = Guid.NewGuid(), TrackId = trackA });
        _db.StreamSessions.Add(new StreamSession { Id = Guid.NewGuid(), TrackId = trackB });

        // Sales: 2 completed + 1 pending for A (pending must NOT count); 0 for B
        _db.Purchases.AddRange(
            new Purchase { Id = Guid.NewGuid(), TrackId = trackA, BuyerId = "b1", Status = "completed" },
            new Purchase { Id = Guid.NewGuid(), TrackId = trackA, BuyerId = "b2", Status = "completed" },
            new Purchase { Id = Guid.NewGuid(), TrackId = trackA, BuyerId = "b3", Status = "pending" });

        await _db.SaveChangesAsync();

        var repo = new TrackRepository(_db);
        var stats = await repo.GetTrackStatsAsync(new[] { trackA, trackB, trackC });

        Assert.Equal(3, stats[trackA].Plays);
        Assert.Equal(2, stats[trackA].Sales);
        Assert.Equal(1, stats[trackB].Plays);
        Assert.Equal(0, stats[trackB].Sales);
        // A track with no activity is still present, zeroed.
        Assert.Equal(0, stats[trackC].Plays);
        Assert.Equal(0, stats[trackC].Sales);
    }

    [Fact]
    public async Task GetTrackStatsAsync_EmptyInput_ReturnsEmpty()
    {
        var repo = new TrackRepository(_db);
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
            _db.StreamSessions.Add(new StreamSession { Id = Guid.NewGuid(), TrackId = trackA });
        _db.Purchases.AddRange(
            new Purchase { Id = Guid.NewGuid(), TrackId = trackA, BuyerId = "b1", Status = "completed" },
            new Purchase { Id = Guid.NewGuid(), TrackId = trackA, BuyerId = "b2", Status = "completed" },
            new Purchase { Id = Guid.NewGuid(), TrackId = trackA, BuyerId = "b3", Status = "pending" });

        await _db.SaveChangesAsync();

        var repo = new CreatorIdentityRepository(_db, Substitute.For<ILogger<CreatorIdentityRepository>>());
        var tracks = await repo.GetTracksByCreatorIdAsync(creatorUuid, 1, 50);

        var a = tracks.Single(t => t.Id == trackA.ToString());
        var b = tracks.Single(t => t.Id == trackB.ToString());
        Assert.Equal(5, a.Plays);
        Assert.Equal(2, a.Sales);
        Assert.Equal(0, b.Plays);
        Assert.Equal(0, b.Sales);
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
            _db.StreamSessions.Add(new StreamSession { Id = Guid.NewGuid(), TrackId = trackId });
        // 2 completed sales (downloads)
        _db.Purchases.AddRange(
            new Purchase { Id = Guid.NewGuid(), TrackId = trackId, BuyerId = "b1", Status = "completed" },
            new Purchase { Id = Guid.NewGuid(), TrackId = trackId, BuyerId = "b2", Status = "completed" });
        // 3 followers
        for (var i = 0; i < 3; i++)
            _db.CreatorFollows.Add(new CreatorFollow { Id = Guid.NewGuid(), CreatorId = creatorUuid, FollowerId = $"f{i}" });

        await _db.SaveChangesAsync();

        var repo = new CreatorProfileRepository(_db);
        var profile = await repo.GetBySlugAsync("metricstar");

        Assert.NotNull(profile);
        Assert.Equal(4, profile!.Stats.TotalPlays);
        Assert.Equal(2, profile.Stats.TotalDownloads);
        Assert.Equal(3, profile.Stats.FollowerCount);
    }
}
