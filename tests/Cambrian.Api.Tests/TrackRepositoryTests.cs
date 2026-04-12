using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.Tests;

public sealed class TrackRepositoryTests : IDisposable
{
    private readonly CambrianDbContext _db;
    private readonly TrackRepository _repository;

    public TrackRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new CambrianDbContext(options);
        _repository = new TrackRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task UpdateAsync_DoesNotReattachDuplicateApplicationUser_WhenTrackIsDetached()
    {
        var userId = Guid.NewGuid().ToString();
        var trackId = Guid.NewGuid();

        _db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = "creator",
            NormalizedUserName = "CREATOR",
            Email = "creator@test.com",
            NormalizedEmail = "CREATOR@TEST.COM",
            Role = "Creator",
            Tier = "creator",
            Status = "active"
        });

        _db.Tracks.Add(new Track
        {
            Id = trackId,
            CambrianTrackId = "CAMB-TRK-UPDATET1",
            CreatorId = userId,
            Title = "Before",
            Description = "Old description",
            Genre = "Old",
            NonExclusivePriceCents = 1000,
            ExclusivePriceCents = 2000,
            CopyrightBuyoutPriceCents = 3000,
            Status = "available",
            Visibility = "public",
            Tags = new List<string> { "old-tag" }
        });

        await _db.SaveChangesAsync();

        _ = await _db.Users.FirstAsync(u => u.Id == userId);

        var detachedTrack = new Track
        {
            Id = trackId,
            CambrianTrackId = "CAMB-TRK-UPDATET1",
            CreatorId = userId,
            Title = "After",
            Description = "New description",
            Genre = "Hip-Hop",
            NonExclusivePriceCents = 1500,
            ExclusivePriceCents = 2500,
            CopyrightBuyoutPriceCents = 3500,
            Status = "available",
            Visibility = "public",
            Tags = new List<string> { "drill", "dark" },
            Creator = new ApplicationUser
            {
                Id = userId,
                UserName = "creator",
                NormalizedUserName = "CREATOR",
                Email = "creator@test.com",
                NormalizedEmail = "CREATOR@TEST.COM"
            }
        };

        var exception = await Record.ExceptionAsync(() => _repository.UpdateAsync(detachedTrack));

        Assert.Null(exception);

        var updated = await _db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        Assert.Equal("After", updated.Title);
        Assert.Equal("New description", updated.Description);
        Assert.Equal(1500, updated.NonExclusivePriceCents);
        Assert.Equal(["drill", "dark"], updated.Tags);
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletesTrack_AndPreservesPurchaseAndLibraryHistory()
    {
        var creatorId = Guid.NewGuid().ToString();
        var buyerId = Guid.NewGuid().ToString();
        var trackId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();

        _db.Users.AddRange(
            new ApplicationUser
            {
                Id = creatorId,
                UserName = "creator",
                NormalizedUserName = "CREATOR",
                Email = "creator@test.com",
                NormalizedEmail = "CREATOR@TEST.COM",
                Role = "Creator",
                Tier = "creator",
                Status = "active"
            },
            new ApplicationUser
            {
                Id = buyerId,
                UserName = "buyer",
                NormalizedUserName = "BUYER",
                Email = "buyer@test.com",
                NormalizedEmail = "BUYER@TEST.COM",
                Role = "Listener",
                Tier = "free",
                Status = "active"
            });

        _db.Tracks.Add(new Track
        {
            Id = trackId,
            CambrianTrackId = "CAMB-TRK-DELETE01",
            CreatorId = creatorId,
            Title = "Delete Me",
            Genre = "Hip-Hop",
            NonExclusivePriceCents = 1000,
            ExclusivePriceCents = 2000,
            CopyrightBuyoutPriceCents = 3000,
            Status = "available",
            Visibility = "public"
        });

        _db.Purchases.Add(new Purchase
        {
            Id = purchaseId,
            BuyerId = buyerId,
            TrackId = trackId,
            AmountCents = 1000,
            Status = "completed"
        });

        _db.Library.Add(new LibraryItem
        {
            Id = libraryId,
            UserId = buyerId,
            TrackId = trackId,
            PurchaseId = purchaseId,
            Title = "Delete Me"
        });

        await _db.SaveChangesAsync();

        await _repository.DeleteAsync(trackId);

        var track = await _db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        var purchase = await _db.Purchases.AsNoTracking().SingleAsync(p => p.Id == purchaseId);
        var libraryItem = await _db.Library.AsNoTracking().SingleAsync(l => l.Id == libraryId);

        Assert.Equal("hidden", track.Visibility);
        Assert.Equal("removed", track.Status);
        Assert.Equal(trackId, purchase.TrackId);
        Assert.Equal(trackId, libraryItem.TrackId);
    }
}
