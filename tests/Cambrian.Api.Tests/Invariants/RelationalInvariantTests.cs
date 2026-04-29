using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.Tests.Invariants;

public sealed class RelationalInvariantTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CambrianDbContext _db;

    public RelationalInvariantTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new CambrianDbContext(options);
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Purchase_StripeSessionId_MustBeUnique()
    {
        var user = await SeedUserAsync("unique-session@test.com");
        var creator = await SeedUserAsync("unique-session-creator@test.com");
        var trackId = await SeedTrackAsync(creator.Id);

        _db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = user.Id,
            TrackId = trackId,
            Status = "completed",
            StripeSessionId = "cs_unique_session"
        });
        await _db.SaveChangesAsync();

        _db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = user.Id,
            TrackId = trackId,
            Status = "completed",
            StripeSessionId = "cs_unique_session"
        });

        var act = () => _db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task LibraryItem_UserTrackPair_MustBeUnique()
    {
        var user = await SeedUserAsync("unique-library@test.com");
        var creator = await SeedUserAsync("unique-library-creator@test.com");
        var trackId = await SeedTrackAsync(creator.Id);

        _db.Library.Add(new LibraryItem
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TrackId = trackId,
            Title = "Beat",
            Artist = "Artist"
        });
        await _db.SaveChangesAsync();

        _db.Library.Add(new LibraryItem
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TrackId = trackId,
            Title = "Beat",
            Artist = "Artist"
        });

        var act = () => _db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task LibraryItem_CannotReferenceMissingTrack()
    {
        var user = await SeedUserAsync("orphan-check@test.com");

        _db.Library.Add(new LibraryItem
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TrackId = Guid.NewGuid(),
            Title = "Orphan",
            Artist = "Nobody"
        });

        var act = () => _db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    private async Task<ApplicationUser> SeedUserAsync(string email)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant()
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    private async Task<Guid> SeedTrackAsync(string creatorId)
    {
        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track
        {
            Id = trackId,
            CambrianTrackId = $"CAMB-TRK-{trackId.ToString()[..8].ToUpperInvariant()}",
            Title = "Invariant Beat",
            CreatorId = creatorId,
            AudioUrl = "tracks/invariant-beat.mp3"
        });
        await _db.SaveChangesAsync();
        return trackId;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
