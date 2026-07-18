using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Cambrian.Persistence.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Guards the shared qualified-play read model used by catalog, activity, and
/// public aggregate surfaces. Raw StreamSession rows must not influence these reads.
/// </summary>
public sealed class QualifiedPlayReadConsistencyTests : IDisposable
{
    private readonly CambrianDbContext _db;

    public QualifiedPlayReadConsistencyTests()
    {
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new CambrianDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task TrendingReaders_UseSameQualifiedRanking_FilterIneligible_AndRefreshImmediately()
    {
        var firstTie = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondTie = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var olderTie = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var lower = Guid.Parse("00000000-0000-0000-0000-000000000004");
        var hidden = Guid.Parse("00000000-0000-0000-0000-000000000005");
        var removed = Guid.Parse("00000000-0000-0000-0000-000000000006");

        var newer = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
        var older = newer.AddDays(-1);
        _db.Users.Add(new ApplicationUser
        {
            Id = "qualified-reader-creator",
            UserName = "qualified-reader-creator",
            Email = "qualified-reader@test.local",
        });
        _db.Tracks.AddRange(
            PublicTrack(firstTie, "First tie", newer),
            PublicTrack(secondTie, "Second tie", newer),
            PublicTrack(olderTie, "Older tie", older),
            PublicTrack(lower, "Lower", newer),
            PublicTrack(hidden, "Hidden", newer, visibility: "hidden"),
            PublicTrack(removed, "Removed", newer, status: "removed"));
        _db.TrackStats.AddRange(
            Stat(firstTie, 10),
            Stat(secondTie, 10),
            Stat(olderTie, 10),
            Stat(lower, 5),
            Stat(hidden, 1_000),
            Stat(removed, 1_000));

        // Raw starts are deliberately ignored by every public count/ranking reader.
        _db.StreamSessions.Add(new StreamSession
        {
            Id = Guid.NewGuid(),
            TrackId = lower,
            StartedAt = newer,
        });
        await _db.SaveChangesAsync();

        var activity = new ActivityService(_db, Substitute.For<IFeatureFlagService>());
        var activityRows = await activity.GetTrendingAsync(CancellationToken.None);
        var catalogRows = await new TrackRepository(_db).BrowseAsync(
            1, 20, null, null, "trending", null, null, null, null);

        var expected = new[] { firstTie, secondTie, olderTie, lower };
        Assert.Equal(expected, activityRows.Select(x => x.TrackId));
        Assert.Equal(expected, catalogRows.Select(x => x.Id));
        Assert.Equal(new long[] { 10, 10, 10, 5 }, activityRows.Select(x => x.Plays));
        Assert.Equal(4, await new TrackRepository(_db).CountTrendingAsync());

        // No five-minute activity cache: a committed projection update is visible next read.
        var lowerStats = await _db.TrackStats.SingleAsync(x => x.TrackId == lower);
        lowerStats.PlayCount = 20;
        await _db.SaveChangesAsync();

        var refreshed = await activity.GetTrendingAsync(CancellationToken.None);
        Assert.Equal(lower, refreshed[0].TrackId);
        Assert.Equal(20, refreshed[0].Plays);
    }

    [Fact]
    public async Task PublicPlatformCounts_SumOnlyEligiblePublicTrackStats_AsBigint()
    {
        var eligible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var expected = (long)int.MaxValue + 99;
        _db.Tracks.AddRange(
            PublicTrack(eligible, "Eligible", DateTime.UtcNow),
            PublicTrack(hidden, "Hidden", DateTime.UtcNow, visibility: "hidden"));
        _db.TrackStats.AddRange(Stat(eligible, expected), Stat(hidden, 10_000));
        await _db.SaveChangesAsync();

        var counts = await new PublicDirectoryRepository(_db).GetPlatformCountsAsync();

        Assert.Equal(1, counts.TrackCount);
        Assert.Equal(expected, counts.TotalPlays);
    }

    private static Track PublicTrack(
        Guid id,
        string title,
        DateTime createdAt,
        string visibility = "public",
        string status = "available") => new()
        {
            Id = id,
            CambrianTrackId = $"CAMB-TRK-{id.ToString("N")[..8].ToUpperInvariant()}",
            Title = title,
            CreatorId = "qualified-reader-creator",
            Visibility = visibility,
            Status = status,
            AudioUrl = $"tracks/{id:D}.mp3",
            CreatedAt = createdAt,
        };

    private static TrackStat Stat(Guid trackId, long plays) => new()
    {
        TrackId = trackId,
        PlayCount = plays,
        UpdatedAt = DateTime.UtcNow,
    };
}
