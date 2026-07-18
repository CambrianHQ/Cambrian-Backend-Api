using Cambrian.Application.DTOs.Catalog;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class CreatorIdentityRepositoryCompatibilityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CambrianDbContext _db;
    private readonly CreatorIdentityRepository _repository;

    public CreatorIdentityRepositoryCompatibilityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new CambrianDbContext(options);
        _repository = new CreatorIdentityRepository(_db, Substitute.For<ILogger<CreatorIdentityRepository>>());

        CreateLegacySchema();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetTracksByCreatorIdAsync_WorksAgainstLegacyTrackSchemaWithoutTaxonomyColumns()
    {
        var creatorId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString();
        var createdAt = DateTime.UtcNow;

        _db.Creators.Add(new Creator
        {
            Id = creatorId,
            UserId = userId,
            Username = "aiden",
            DisplayName = "Aiden",
            Bio = "",
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        });
        _db.CreatorProfiles.Add(new CreatorProfile
        {
            Id = profileId,
            UserId = userId,
            Slug = "aiden",
            ProfileImageUrl = "profile.jpg",
            Bio = "",
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        });
        await _db.SaveChangesAsync();

        ExecuteNonQuery($$"""
            INSERT INTO "Tracks" ("Id", "CambrianTrackId", "Title", "Description", "Genre", "Price", "NonExclusivePriceCents", "ExclusivePriceCents", "CopyrightBuyoutPriceCents", "ExclusiveSold", "Status", "CopyrightOwnerId", "LicenseType", "Duration", "AudioUrl", "CoverArtUrl", "CreatorId", "CreatorUuid", "Visibility", "CreatedAt")
            VALUES ('{{trackId}}', 'CAMB-TRK-LEGACY01', 'Legacy Track', 'still available', 'Trap', 29.99, 0, 4999, 0, 0, 'available', NULL, 'non-exclusive', '3:10', 'audio.mp3', 'cover.jpg', '{{userId}}', '{{creatorId}}', 'public', '{{createdAt:O}}');
            """);

        List<TrackResponse> tracks = await _repository.GetTracksByCreatorIdAsync(creatorId, 1, 10);

        var track = Assert.Single(tracks);
        Assert.Equal(trackId.ToString(), track.Id);
        Assert.Equal("CAMB-TRK-LEGACY01", track.CambrianTrackId);
        Assert.Equal("Legacy Track", track.Title);
        Assert.Equal("Trap", track.Genre);
        Assert.Equal(29.99m, track.Price);
        Assert.Equal(29.99m, track.NonExclusivePrice);
        Assert.Equal("aiden", track.CreatorSlug);
        Assert.Equal("Aiden", track.Artist);
        Assert.Equal("profile.jpg", track.CreatorProfileImageUrl);
    }

    private void CreateLegacySchema()
    {
        ExecuteNonQuery(
            """
            CREATE TABLE "Creators" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "UserId" TEXT NOT NULL,
                "Username" TEXT NOT NULL,
                "DisplayName" TEXT NULL,
                "Bio" TEXT NOT NULL,
                "ProfileImageUrl" TEXT NULL,
                "CoverImageUrl" TEXT NULL,
                "SocialLinks" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);

        // Mirror the full CreatorProfile column set so EF's INSERT of a
        // CreatorProfile entity succeeds. StudioSetup + JourneyEntries were
        // added to the entity (commit 63029b7) as nullable JSON columns —
        // kept here so this legacy-schema fixture stays in sync. (The test's
        // "legacy" intent is about ABSENT Track taxonomy columns, not these.)
        ExecuteNonQuery(
            """
            CREATE TABLE "CreatorProfiles" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "UserId" TEXT NOT NULL,
                "Slug" TEXT NOT NULL,
                "BannerImageUrl" TEXT NULL,
                "ProfileImageUrl" TEXT NULL,
                "Bio" TEXT NOT NULL,
                "Niche" TEXT NULL,
                "Genres" TEXT NULL,
                "SocialLinks" TEXT NULL,
                "StudioSetup" TEXT NULL,
                "JourneyEntries" TEXT NULL,
                "ShowEarnings" INTEGER NOT NULL DEFAULT 0,
                "ShowDownloadStats" INTEGER NOT NULL DEFAULT 0,
                "PinnedTrackIds" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);

        ExecuteNonQuery(
            """
            CREATE TABLE "Tracks" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "CambrianTrackId" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "Description" TEXT NULL,
                "Genre" TEXT NULL,
                "Price" REAL NOT NULL,
                "NonExclusivePriceCents" INTEGER NOT NULL,
                "ExclusivePriceCents" INTEGER NOT NULL,
                "CopyrightBuyoutPriceCents" INTEGER NOT NULL,
                "ExclusiveSold" INTEGER NOT NULL,
                "Status" TEXT NOT NULL,
                "CopyrightOwnerId" TEXT NULL,
                "LicenseType" TEXT NULL,
                "Duration" TEXT NULL,
                "AudioUrl" TEXT NULL,
                "CoverArtUrl" TEXT NULL,
                "CreatorId" TEXT NOT NULL,
                "CreatorUuid" TEXT NULL,
                "Visibility" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """);

        // Engagement enrichment reads these tables after the legacy track projection.
        // Keep them empty; this compatibility test only needs the tables to exist so
        // the repository can return zero plays/sales/authorship metadata.
        ExecuteNonQuery(
            """
            CREATE TABLE "StreamSessions" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "TrackId" TEXT NOT NULL
            );
            """);

        ExecuteNonQuery(
            """
            CREATE TABLE "Purchases" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "TrackId" TEXT NOT NULL,
                "Status" TEXT NOT NULL
            );
            """);

        ExecuteNonQuery(
            """
            CREATE TABLE "AuthorshipRecords" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "TrackId" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "IssuedAt" TEXT NULL
            );
            """);
    }

    private void ExecuteNonQuery(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
