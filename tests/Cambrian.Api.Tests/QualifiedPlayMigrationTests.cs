using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>Upgrade proof for the one-time historical baseline snapshot.</summary>
public sealed class QualifiedPlayMigrationTests : IClassFixture<RelationalCambrianApiFixture>
{
    private const string PreviousMigration = "20260713021256_HardenPaymentFulfillmentAudit";
    private readonly RelationalCambrianApiFixture _fixture;

    public QualifiedPlayMigrationTests(RelationalCambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Upgrade_PreservesStoredCountsAndSnapshotsSessionsWithoutFabricatingEvents()
    {
        Assert.Equal("PostgreSQL", _fixture.DatabaseProvider);

        var email = $"play-migration-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(email);
        var creatorId = await _fixture.GetUserIdAsync(email);
        var existingStatsTrack = await _fixture.SeedTrackAsync(creatorId, "Existing count");
        var sessionsOnlyTrack = await _fixture.SeedTrackAsync(creatorId, "Sessions only");
        var now = DateTime.SpecifyKind(new DateTime(2026, 7, 13, 12, 0, 0), DateTimeKind.Utc);

        using (var seedScope = _fixture.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            db.TrackStats.Add(new TrackStat
            {
                TrackId = existingStatsTrack,
                PlayCount = 7,
                LegacyPlayCount = 0,
                QualifiedPlayCount = 0,
                UpdatedAt = now,
            });
            db.StreamSessions.AddRange(
                LegacyShapedSession(existingStatsTrack, now.AddMinutes(-3)),
                LegacyShapedSession(sessionsOnlyTrack, now.AddMinutes(-2)),
                LegacyShapedSession(sessionsOnlyTrack, now.AddMinutes(-1)));
            await db.SaveChangesAsync();
        }

        // Exercise both rollback and forward migration on the exact generated artifacts.
        using (var downScope = _fixture.Services.CreateScope())
        {
            var db = downScope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            await db.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }
        using (var upScope = _fixture.Services.CreateScope())
        {
            var db = upScope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            await db.Database.MigrateAsync();
        }

        using var assertScope = _fixture.Services.CreateScope();
        var fresh = assertScope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var preserved = await fresh.TrackStats.AsNoTracking()
            .SingleAsync(row => row.TrackId == existingStatsTrack);
        var snapshotted = await fresh.TrackStats.AsNoTracking()
            .SingleAsync(row => row.TrackId == sessionsOnlyTrack);

        Assert.Equal(7, preserved.LegacyPlayCount);
        Assert.Equal(7, preserved.PlayCount);
        Assert.Equal(0, preserved.QualifiedPlayCount);
        Assert.Equal(2, snapshotted.LegacyPlayCount);
        Assert.Equal(2, snapshotted.PlayCount);
        Assert.Equal(0, snapshotted.QualifiedPlayCount);
        Assert.All(await fresh.StreamSessions.AsNoTracking().ToListAsync(),
            row => Assert.Equal("legacy_unqualified", row.QualificationStatus));
        Assert.Empty(await fresh.QualifiedPlayEvents.AsNoTracking().ToListAsync());
    }

    private static StreamSession LegacyShapedSession(Guid trackId, DateTime startedAt) => new()
    {
        Id = Guid.NewGuid(),
        TrackId = trackId,
        StartedAt = startedAt,
        QualificationStatus = "pending",
    };
}
