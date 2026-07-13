using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Cambrian.Persistence.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;

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
        var playCounts = new PlayCountService(_db, new MemoryCache(new MemoryCacheOptions()), Substitute.For<ILogger<PlayCountService>>());
        _repository = new TrackRepository(_db, playCounts);
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

    // Marketplace search used to filter on Title only, so creator-name and
    // description searches returned nothing. These tests pin the expanded
    // search behavior so future edits can't quietly regress it.
    [Theory]
    [InlineData("aurora", new[] { "Aurora Loop" })]                       // title hit
    [InlineData("velvet midnight", new string[0])]                         // multi-word phrase that doesn't match anywhere
    [InlineData("midnight signal", new[] { "Midnight Signal" })]           // exact title
    [InlineData("dreamy synthwave", new[] { "Aurora Loop" })]              // description hit
    [InlineData("bella", new[] { "Aurora Loop", "Midnight Signal" })]      // creator displayName hit
    [InlineData("bellanova", new[] { "Aurora Loop", "Midnight Signal" })] // creator username hit
    [InlineData("BELLA", new[] { "Aurora Loop", "Midnight Signal" })]     // case-insensitive
    [InlineData("electronic", new[] { "Aurora Loop", "Midnight Signal" })] // genre hit
    [InlineData("orchestral", new[] { "Fields of Grace" })]                // genre hit (separate creator)
    [InlineData("dark", new[] { "Midnight Signal" })]                       // mood hit
    public async Task BrowseAsync_SearchesAcrossTitleDescriptionAndCreator(string search, string[] expectedTitles)
    {
        await SeedSearchableCatalog();

        var results = await _repository.BrowseAsync(page: 1, pageSize: 50, genre: null, search: search);
        var titles = results.Select(t => t.Title).OrderBy(t => t).ToArray();
        Assert.Equal(expectedTitles.OrderBy(t => t).ToArray(), titles);
    }

    private async Task SeedSearchableCatalog()
    {
        var bellaUserId = Guid.NewGuid().ToString();
        var griffinUserId = Guid.NewGuid().ToString();

        _db.Users.AddRange(
            new ApplicationUser
            {
                Id = bellaUserId,
                UserName = "bellanova",
                NormalizedUserName = "BELLANOVA",
                Email = "bella@test.com",
                NormalizedEmail = "BELLA@TEST.COM",
                DisplayName = "Bella Nova",
                Role = "Creator",
                Tier = "creator",
                Status = "active"
            },
            new ApplicationUser
            {
                Id = griffinUserId,
                UserName = "griffincole",
                NormalizedUserName = "GRIFFINCOLE",
                Email = "griffin@test.com",
                NormalizedEmail = "GRIFFIN@TEST.COM",
                DisplayName = "Griffin Cole",
                Role = "Creator",
                Tier = "creator",
                Status = "active"
            });

        var bellaCreatorId = Guid.NewGuid();
        var griffinCreatorId = Guid.NewGuid();
        _db.Creators.AddRange(
            new Creator { Id = bellaCreatorId, UserId = bellaUserId, Username = "bellanova", DisplayName = "Bella Nova" },
            new Creator { Id = griffinCreatorId, UserId = griffinUserId, Username = "griffincole", DisplayName = "Griffin Cole" });

        _db.Tracks.AddRange(
            new Track
            {
                Id = Guid.NewGuid(),
                CambrianTrackId = "CAMB-TRK-AURORA01",
                CreatorId = bellaUserId,
                CreatorUuid = bellaCreatorId,
                Title = "Aurora Loop",
                Description = "Dreamy synthwave with ambient textures",
                Genre = "electronic",
                NonExclusivePriceCents = 299,
                ExclusivePriceCents = 1499,
                CopyrightBuyoutPriceCents = 5999,
                Status = "available",
                Visibility = "public"
            },
            new Track
            {
                Id = Guid.NewGuid(),
                CambrianTrackId = "CAMB-TRK-MIDNT001",
                CreatorId = bellaUserId,
                CreatorUuid = bellaCreatorId,
                Title = "Midnight Signal",
                Description = "Late-night driving track",
                Genre = "electronic",
                Mood = "dark",
                NonExclusivePriceCents = 449,
                ExclusivePriceCents = 1999,
                CopyrightBuyoutPriceCents = 7999,
                Status = "available",
                Visibility = "public"
            },
            new Track
            {
                Id = Guid.NewGuid(),
                CambrianTrackId = "CAMB-TRK-FIELDS01",
                CreatorId = griffinUserId,
                CreatorUuid = griffinCreatorId,
                Title = "Fields of Grace",
                Description = "Orchestral cue for emotional moments",
                Genre = "orchestral",
                NonExclusivePriceCents = 599,
                ExclusivePriceCents = 2499,
                CopyrightBuyoutPriceCents = 9999,
                Status = "available",
                Visibility = "public"
            });

        await _db.SaveChangesAsync();
    }
}
