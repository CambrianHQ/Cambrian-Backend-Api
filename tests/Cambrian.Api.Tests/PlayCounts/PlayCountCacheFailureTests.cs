using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests.PlayCounts;

/// <summary>
/// This stack has no Redis (see CLAUDE.md) — the only cache in front of play counts is the
/// IMemoryCache used by PlayCountService.GetPlatformTotalPlaysAsync, and it exists purely as a
/// best-effort optimization for that one genuinely-aggregate query. These tests prove the
/// architectural point the task cares about regardless of which cache technology sits there:
/// a broken/unavailable cache can never corrupt or block a read of the durable Postgres truth
/// (TrackStats/CreatorStats) — every read either gets a correct cached value or transparently
/// falls through to a correct live one.
/// </summary>
public sealed class PlayCountCacheFailureTests : IDisposable
{
    private readonly CambrianDbContext _db;

    public PlayCountCacheFailureTests()
    {
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new CambrianDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>A minimal, hand-written IMemoryCache that always throws — simpler and more
    /// readable here than mocking NSubstitute's out-parameter TryGetValue signature.</summary>
    private sealed class AlwaysThrowingMemoryCache : IMemoryCache
    {
        public bool TryGetValue(object key, out object? value) =>
            throw new InvalidOperationException("Simulated cache backend failure (e.g. Redis unreachable).");

        public ICacheEntry CreateEntry(object key) =>
            throw new InvalidOperationException("Simulated cache backend failure (e.g. Redis unreachable).");

        public void Remove(object key) =>
            throw new InvalidOperationException("Simulated cache backend failure (e.g. Redis unreachable).");

        public void Dispose() { }
    }

    private async Task SeedTracksAsync(params (Guid TrackId, long PlayCount)[] tracks)
    {
        foreach (var (trackId, playCount) in tracks)
        {
            _db.Tracks.Add(new Track { Id = trackId, Title = "Cache Test Beat", CreatorId = "creator-cache-1" });
            _db.TrackStats.Add(new TrackStat { TrackId = trackId, PlayCount = playCount, UpdatedAt = DateTime.UtcNow });
        }
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetPlatformTotalPlaysAsync_WhenCacheThrowsOnRead_FallsBackToPostgresDirectly()
    {
        var trackA = Guid.NewGuid();
        var trackB = Guid.NewGuid();
        await SeedTracksAsync((trackA, 10), (trackB, 25));

        var service = new PlayCountService(_db, new AlwaysThrowingMemoryCache(), Substitute.For<ILogger<PlayCountService>>());

        var total = await service.GetPlatformTotalPlaysAsync();

        Assert.Equal(35L, total);
    }

    [Fact]
    public async Task GetPlatformTotalPlaysAsync_RepeatedReadsWithThrowingCache_StayCorrectEveryTime()
    {
        // A cache that never succeeds means every read falls through live — still correct, and
        // still reflects changes (nothing was ever actually cached to go stale).
        var trackId = Guid.NewGuid();
        await SeedTracksAsync((trackId, 5));
        var service = new PlayCountService(_db, new AlwaysThrowingMemoryCache(), Substitute.For<ILogger<PlayCountService>>());

        Assert.Equal(5L, await service.GetPlatformTotalPlaysAsync());

        var stat = await _db.TrackStats.SingleAsync(s => s.TrackId == trackId);
        stat.PlayCount = 9;
        await _db.SaveChangesAsync();

        Assert.Equal(9L, await service.GetPlatformTotalPlaysAsync());
    }

    [Fact]
    public async Task GetPlatformTotalPlaysAsync_WithAWorkingCache_StillReturnsTheCorrectValue()
    {
        // Sanity check for the non-failure path, so the failure tests above are meaningfully
        // contrasted against normal behavior rather than the only case ever exercised.
        var trackId = Guid.NewGuid();
        await SeedTracksAsync((trackId, 42));
        var service = new PlayCountService(_db, new MemoryCache(new MemoryCacheOptions()), Substitute.For<ILogger<PlayCountService>>());

        Assert.Equal(42L, await service.GetPlatformTotalPlaysAsync());
    }

    [Fact]
    public async Task PerTrackAndCreatorReads_AreUnaffectedByCacheFailure_BecauseTheyNeverUseIt()
    {
        // Per-track/creator lookups are a single indexed row read — cheap enough to always read
        // live rather than risk a stale-after-write cache window (see PlayCountService). A
        // broken cache injected into the service must not affect them at all.
        var creatorUuid = Guid.NewGuid();
        const string userId = "creator-cache-2";
        var trackId = Guid.NewGuid();
        _db.Creators.Add(new Creator { Id = creatorUuid, UserId = userId, Username = "cachetest2", DisplayName = "CacheTest2" });
        _db.Tracks.Add(new Track { Id = trackId, Title = "Beat", CreatorId = userId, CreatorUuid = creatorUuid });
        _db.TrackStats.Add(new TrackStat { TrackId = trackId, PlayCount = 17, UpdatedAt = DateTime.UtcNow });
        _db.CreatorStats.Add(new CreatorStat { CreatorId = creatorUuid, TotalPlays = 17, UpdatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var service = new PlayCountService(_db, new AlwaysThrowingMemoryCache(), Substitute.For<ILogger<PlayCountService>>());

        Assert.Equal(17L, await service.GetTrackPlayCountAsync(trackId));
        Assert.Equal(17L, await service.GetCreatorTotalPlaysAsync(userId, creatorUuid));
        var batch = await service.GetTrackPlayCountsAsync(new[] { trackId });
        Assert.Equal(17L, batch[trackId]);
    }
}
