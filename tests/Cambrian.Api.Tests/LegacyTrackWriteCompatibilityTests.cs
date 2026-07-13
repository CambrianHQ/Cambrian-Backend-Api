using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Cambrian.Persistence.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class LegacyTrackWriteCompatibilityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CambrianDbContext _db;
    private readonly TrackRepository _repository;

    public LegacyTrackWriteCompatibilityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new CambrianDbContext(options);
        var playCounts = new PlayCountService(_db, new MemoryCache(new MemoryCacheOptions()), Substitute.For<ILogger<PlayCountService>>());
        _repository = new TrackRepository(_db, playCounts);

        CreateLegacySchema();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task AddAsync_WorksAgainstLegacyTrackSchemaWithoutTaxonomyColumns()
    {
        var trackId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;

        var track = new Track
        {
            Id = trackId,
            CambrianTrackId = "CAMB-TRK-LEGACYA1",
            Title = "Legacy Upload",
            Description = "compat insert",
            Genre = "Drill",
            PrimaryGenre = "Hip-Hop",
            Subgenre = "Drill",
            Mood = "Dark",
            Tempo = "140",
            Instrumental = true,
            Price = 29.99m,
            Duration = "3:12",
            LicenseType = "non-exclusive",
            AudioUrl = "audio.mp3",
            CoverArtUrl = "cover.jpg",
            NonExclusivePriceCents = 2999,
            ExclusivePriceCents = 4999,
            CopyrightBuyoutPriceCents = 9999,
            Status = "available",
            Visibility = "public",
            CreatedAt = createdAt,
            CreatorId = "creator-1",
            Tags = ["dark", "drill"],
            UseCase = "vlog",
            TrendingScore = 1.5m
        };

        await _repository.AddAsync(track);

        Assert.Equal("Legacy Upload", GetTrackValue<string>(trackId, "Title"));
        Assert.Equal("Drill", GetTrackValue<string>(trackId, "Genre"));
        Assert.Equal("dark,drill", GetTrackValue<string>(trackId, "Tags"));
        Assert.Equal("vlog", GetTrackValue<string>(trackId, "UseCase"));
    }

    [Fact]
    public async Task UpdateAsync_WorksAgainstLegacyTrackSchemaWithoutTaxonomyColumns()
    {
        var trackId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;

        InsertLegacyTrack(trackId, createdAt, title: "Before", genre: "Trap");

        var track = new Track
        {
            Id = trackId,
            CambrianTrackId = "CAMB-TRK-LEGACYU1",
            Title = "After",
            Description = "compat update",
            Genre = "Drill",
            PrimaryGenre = "Hip-Hop",
            Subgenre = "Drill",
            Mood = "Moody",
            Tempo = "150",
            Instrumental = false,
            Price = 49.99m,
            Duration = "2:58",
            LicenseType = "exclusive",
            AudioUrl = "updated.mp3",
            CoverArtUrl = "updated.jpg",
            NonExclusivePriceCents = 4999,
            ExclusivePriceCents = 6999,
            CopyrightBuyoutPriceCents = 10999,
            ExclusiveSold = false,
            Status = "available",
            Visibility = "limited",
            CreatedAt = createdAt,
            CreatorId = "creator-1",
            Tags = ["updated", "drill"],
            UseCase = "podcast",
            TrendingScore = 3.25m
        };

        await _repository.UpdateAsync(track);

        Assert.Equal("After", GetTrackValue<string>(trackId, "Title"));
        Assert.Equal("Drill", GetTrackValue<string>(trackId, "Genre"));
        Assert.Equal("limited", GetTrackValue<string>(trackId, "Visibility"));
        Assert.Equal("updated,drill", GetTrackValue<string>(trackId, "Tags"));
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletesAgainstLegacyTrackSchemaWithoutTaxonomyColumns()
    {
        var trackId = Guid.NewGuid();
        InsertLegacyTrack(trackId, DateTime.UtcNow, title: "Delete Me", genre: "Trap");

        await _repository.DeleteAsync(trackId);

        Assert.Equal("hidden", GetTrackValue<string>(trackId, "Visibility"));
        Assert.Equal("removed", GetTrackValue<string>(trackId, "Status"));
    }

    private void CreateLegacySchema()
    {
        ExecuteNonQuery(
            """
            CREATE TABLE "Tracks" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "CambrianTrackId" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "Description" TEXT NULL,
                "Genre" TEXT NULL,
                "Mood" TEXT NULL,
                "Tempo" TEXT NULL,
                "Instrumental" INTEGER NOT NULL,
                "Price" REAL NOT NULL,
                "Duration" TEXT NULL,
                "LicenseType" TEXT NULL,
                "AudioUrl" TEXT NULL,
                "CoverArtUrl" TEXT NULL,
                "NonExclusivePriceCents" INTEGER NOT NULL,
                "ExclusivePriceCents" INTEGER NOT NULL,
                "CopyrightBuyoutPriceCents" INTEGER NOT NULL,
                "ExclusiveSold" INTEGER NOT NULL,
                "Status" TEXT NOT NULL,
                "CopyrightOwnerId" TEXT NULL,
                "CopyrightTransferredAt" TEXT NULL,
                "OriginalCreatorId" TEXT NULL,
                "Visibility" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "CreatorId" TEXT NOT NULL,
                "CreatorUuid" TEXT NULL,
                "Tags" TEXT NOT NULL,
                "UseCase" TEXT NULL,
                "TrendingScore" REAL NOT NULL
            );
            """);
    }

    private void InsertLegacyTrack(Guid trackId, DateTime createdAt, string title, string genre)
    {
        ExecuteNonQuery($$"""
            INSERT INTO "Tracks" (
                "Id",
                "CambrianTrackId",
                "Title",
                "Description",
                "Genre",
                "Mood",
                "Tempo",
                "Instrumental",
                "Price",
                "Duration",
                "LicenseType",
                "AudioUrl",
                "CoverArtUrl",
                "NonExclusivePriceCents",
                "ExclusivePriceCents",
                "CopyrightBuyoutPriceCents",
                "ExclusiveSold",
                "Status",
                "CopyrightOwnerId",
                "CopyrightTransferredAt",
                "OriginalCreatorId",
                "Visibility",
                "CreatedAt",
                "CreatorId",
                "CreatorUuid",
                "Tags",
                "UseCase",
                "TrendingScore"
            )
            VALUES (
                '{{trackId}}',
                'CAMB-TRK-LEGACYU1',
                '{{title}}',
                'seeded',
                '{{genre}}',
                'dark',
                '140',
                1,
                19.99,
                '3:10',
                'non-exclusive',
                'audio.mp3',
                'cover.jpg',
                1999,
                3999,
                0,
                0,
                'available',
                NULL,
                NULL,
                NULL,
                'public',
                '{{createdAt:O}}',
                'creator-1',
                NULL,
                'seeded,tags',
                'gaming',
                0
            );
            """);
    }

    private T GetTrackValue<T>(Guid trackId, string columnName)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""SELECT "{columnName}" FROM "Tracks" WHERE "Id" = $id LIMIT 1;""";
        command.Parameters.AddWithValue("$id", trackId.ToString());
        var value = command.ExecuteScalar();
        Assert.NotNull(value);
        return (T)Convert.ChangeType(value, typeof(T));
    }

    private void ExecuteNonQuery(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
