using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Boost rules for the creator-community layer:
///   - one boost per user per track (DB UNIQUE constraint),
///   - no self-boosts (rejected server-side).
/// (The 7-day Hot-This-Week ranking is covered by its own test when that
///  query slice lands.)
/// </summary>
public sealed class TrackBoostTests
{
    private static ClaimsPrincipal UserWithId(string userId) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "test"));

    [Fact]
    public async Task BoostAsync_RejectsBoostingYourOwnTrack()
    {
        const string userId = "creator-1";
        var trackId = Guid.NewGuid();

        var tracks = Substitute.For<ITrackRepository>();
        tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId,
            CreatorId = userId, // the booster IS the creator
            Visibility = "public",
        });

        var service = new TrackBoostService(
            Substitute.For<ITrackBoostRepository>(),
            tracks,
            Substitute.For<ILogger<TrackBoostService>>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BoostAsync(UserWithId(userId), trackId.ToString()));
    }

    [Fact]
    public void Model_DeclaresUniqueIndexOnUserAndTrack()
    {
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        using var db = new CambrianDbContext(options);

        var entityType = db.Model.FindEntityType(typeof(TrackBoost));
        Assert.NotNull(entityType);

        var uniqueIndex = entityType!.GetIndexes().FirstOrDefault(i =>
            i.IsUnique &&
            i.Properties.Select(p => p.Name).OrderBy(n => n)
                .SequenceEqual(new[] { "TrackId", "UserId" }));

        Assert.NotNull(uniqueIndex);
    }

    [Fact]
    public async Task GetHotSinceAsync_RanksByBoostsInWindow_ExcludingOldAndNonPublic()
    {
        // Relational provider (real GROUP BY/ORDER BY + join), like production.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseSqlite(connection)
            .Options;
        using var db = new CambrianDbContext(options);
        db.Database.EnsureCreated();

        var now = DateTime.UtcNow;
        var within = now.AddDays(-1);
        var old = now.AddDays(-30);

        var trackA = SeedTrack(db, "public");        // 3 boosts in window  → rank 1
        var trackB = SeedTrack(db, "public");        // 1 boost  in window  → rank 2
        var trackOldOnly = SeedTrack(db, "public");  // 2 boosts but all old → excluded
        var hidden = SeedTrack(db, "hidden");         // 5 boosts in window but not public → excluded
        await db.SaveChangesAsync();

        AddBoosts(db, trackA.Id, 3, within);
        AddBoosts(db, trackB.Id, 1, within);
        AddBoosts(db, trackOldOnly.Id, 2, old);
        AddBoosts(db, hidden.Id, 5, within);
        await db.SaveChangesAsync();

        var repo = new TrackBoostRepository(db);
        var since = now.AddDays(-7);

        var ranked = await repo.GetHotSinceAsync(since, 0, 10);

        // Ranked by in-window boost count, highest first; old-only and non-public excluded.
        Assert.Equal(new[] { trackA.Id, trackB.Id }, ranked.Select(r => r.Track.Id).ToArray());
        Assert.Equal(3, ranked[0].BoostCount);
        Assert.Equal(1, ranked[1].BoostCount);
        Assert.Equal(2, await repo.CountHotSinceAsync(since));
    }

    private static Track SeedTrack(CambrianDbContext db, string visibility)
    {
        var userId = "creator-" + Guid.NewGuid();
        // Track.CreatorId is a required FK to AspNetUsers — seed the owner.
        db.Set<ApplicationUser>().Add(new ApplicationUser
        {
            Id = userId,
            UserName = userId,
            Email = userId + "@example.test",
            DisplayName = "Creator",
        });
        var track = new Track
        {
            Id = Guid.NewGuid(),
            Title = "Track",
            CambrianTrackId = ("CT" + Guid.NewGuid().ToString("N"))[..18],
            Visibility = visibility,
            CreatorId = userId,
        };
        db.Tracks.Add(track);
        return track;
    }

    private static void AddBoosts(CambrianDbContext db, Guid trackId, int count, DateTime createdAt)
    {
        for (var i = 0; i < count; i++)
        {
            db.TrackBoosts.Add(new TrackBoost
            {
                UserId = $"u-{trackId}-{i}",
                TrackId = trackId,
                CreatedAt = createdAt,
            });
        }
    }

    [Fact]
    public async Task UniqueConstraint_RejectsDuplicateBoostFromSameUser()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseSqlite(connection)
            .Options;
        using var db = new CambrianDbContext(options);

        // Create just the TrackBoosts table + the UNIQUE index (matches the
        // generated migration), independent of the full Postgres schema.
        db.Database.ExecuteSqlRaw(
            @"CREATE TABLE ""TrackBoosts"" (
                ""Id"" TEXT NOT NULL CONSTRAINT ""PK_TrackBoosts"" PRIMARY KEY,
                ""UserId"" TEXT NOT NULL,
                ""TrackId"" TEXT NOT NULL,
                ""CreatedAt"" TEXT NOT NULL);");
        db.Database.ExecuteSqlRaw(
            @"CREATE UNIQUE INDEX ""ux_track_boosts_user_track"" ON ""TrackBoosts"" (""UserId"", ""TrackId"");");

        const string userId = "fan-1";
        var trackId = Guid.NewGuid();

        db.TrackBoosts.Add(new TrackBoost { UserId = userId, TrackId = trackId });
        await db.SaveChangesAsync();

        // A second boost from the same user for the same track must be rejected
        // by the database, not just by application logic.
        db.TrackBoosts.Add(new TrackBoost { UserId = userId, TrackId = trackId });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
