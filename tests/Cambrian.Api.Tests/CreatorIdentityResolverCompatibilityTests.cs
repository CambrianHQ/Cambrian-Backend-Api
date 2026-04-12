using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class CreatorIdentityResolverCompatibilityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CambrianDbContext _db;
    private readonly CreatorIdentityRepository _repository;

    public CreatorIdentityResolverCompatibilityTests()
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
    public async Task ResolveByLegacyIdentifierAsync_ResolvesCreatorProfileSlug_WhenCreatorUsernameDiffers()
    {
        var creatorId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString();
        var createdAt = DateTime.UtcNow;

        ExecuteNonQuery($$"""
            INSERT INTO "Creators" ("Id", "UserId", "Username", "DisplayName", "Bio", "ProfileImageUrl", "CoverImageUrl", "SocialLinks", "CreatedAt", "UpdatedAt")
            VALUES ('{{creatorId}}', '{{userId}}', 'creator-identity', 'Logan', '', NULL, NULL, NULL, '{{createdAt:O}}', '{{createdAt:O}}');
            """);
        ExecuteNonQuery($$"""
            INSERT INTO "CreatorProfiles" ("Id", "UserId", "Slug", "BannerImageUrl", "ProfileImageUrl", "Bio", "Niche", "SocialLinks", "CreatedAt", "UpdatedAt")
            VALUES ('{{Guid.NewGuid()}}', '{{userId}}', 'loganbryan799', 'banner.jpg', 'profile.jpg', 'bio', NULL, NULL, '{{createdAt:O}}', '{{createdAt:O}}');
            """);

        var creator = await _repository.ResolveByLegacyIdentifierAsync("loganbryan799");

        Assert.NotNull(creator);
        Assert.Equal(creatorId.ToString(), creator!.Id);
        Assert.Equal(userId, creator.UserId);
        Assert.Equal("creator-identity", creator.Username);
        Assert.Equal("profile.jpg", creator.ProfileImageUrl);
    }

    [Fact]
    public async Task ResolveByLegacyIdentifierAsync_ResolvesGuidUserId_AgainstMinimalCreatorProfileSchema()
    {
        var creatorId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString();
        var createdAt = DateTime.UtcNow;

        ExecuteNonQuery($$"""
            INSERT INTO "Creators" ("Id", "UserId", "Username", "DisplayName", "Bio", "ProfileImageUrl", "CoverImageUrl", "SocialLinks", "CreatedAt", "UpdatedAt")
            VALUES ('{{creatorId}}', '{{userId}}', 'another-creator', 'Creator Name', '', NULL, NULL, NULL, '{{createdAt:O}}', '{{createdAt:O}}');
            """);
        ExecuteNonQuery($$"""
            INSERT INTO "CreatorProfiles" ("Id", "UserId", "Slug", "BannerImageUrl", "ProfileImageUrl", "Bio", "Niche", "SocialLinks", "CreatedAt", "UpdatedAt")
            VALUES ('{{Guid.NewGuid()}}', '{{userId}}', 'public-slug', 'banner-two.jpg', 'avatar-two.jpg', 'bio', NULL, NULL, '{{createdAt:O}}', '{{createdAt:O}}');
            """);

        var creator = await _repository.ResolveByLegacyIdentifierAsync(userId);

        Assert.NotNull(creator);
        Assert.Equal(creatorId.ToString(), creator!.Id);
        Assert.Equal(userId, creator.UserId);
        Assert.Equal("avatar-two.jpg", creator.ProfileImageUrl);
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
                "SocialLinks" TEXT NULL,
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

        ExecuteNonQuery(
            """
            CREATE TABLE "Purchases" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "BuyerId" TEXT NOT NULL,
                "TrackId" TEXT NOT NULL,
                "AmountCents" INTEGER NOT NULL,
                "PaymentMethod" TEXT NULL,
                "LicenseType" TEXT NULL,
                "Status" TEXT NOT NULL,
                "UsageType" TEXT NOT NULL,
                "StripeSessionId" TEXT NULL,
                "LicenseId" TEXT NULL,
                "CompletedAt" TEXT NULL,
                "ExpiresAt" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NULL
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
