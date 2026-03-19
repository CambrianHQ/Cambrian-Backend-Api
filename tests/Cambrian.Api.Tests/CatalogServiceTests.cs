using Cambrian.Application.Services;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class CatalogServiceTests
{
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly CatalogService _sut;

    public CatalogServiceTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        var profiles = Substitute.For<ICreatorProfileRepository>();
        _sut = new CatalogService(_tracks, users, profiles);
    }

    [Fact]
    public async Task GetTrackAsync_ReturnsNull_WhenIdIsNotGuid()
    {
        var result = await _sut.GetTrackAsync("not-a-guid");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTrackAsync_ReturnsNull_WhenTrackNotFound()
    {
        var id = Guid.NewGuid();
        _tracks.GetByIdAsync(id).Returns((Track?)null);

        var result = await _sut.GetTrackAsync(id.ToString());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTrackAsync_MapsAllPricingFields()
    {
        var id = Guid.NewGuid();
        var track = new Track
        {
            Id = id,
            Title = "Beat 1",
            Description = "Chill",
            Genre = "Lo-fi",
            Price = 29.99m,
            NonExclusivePriceCents = 2999,
            ExclusivePriceCents = 49900,
            ExclusiveSold = false,
            LicenseType = "non-exclusive",
            Duration = "3:30",
            AudioUrl = "https://cdn.test/beat.mp3",
            CreatorId = "creator-1",
            Creator = new ApplicationUser { DisplayName = "DJ Test", Email = "dj@test.com" },
            CreatedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        _tracks.GetByIdAsync(id).Returns(track);

        var result = await _sut.GetTrackAsync(id.ToString());

        Assert.NotNull(result);
        Assert.Equal(id.ToString(), result!.Id);
        Assert.Equal("Beat 1", result.Title);
        Assert.Equal(29.99m, result.Price);
        Assert.Equal(29.99m, result.NonExclusivePrice);
        Assert.Equal(499.00m, result.ExclusivePrice);
        Assert.False(result.ExclusiveSold);
        Assert.Equal("DJ Test", result.Artist);
        Assert.Equal("Lo-fi", result.Genre);
    }

    [Fact]
    public async Task GetTrackAsync_UsesEmail_WhenDisplayNameIsNull()
    {
        var id = Guid.NewGuid();
        var track = new Track
        {
            Id = id,
            Title = "Beat 2",
            Price = 10,
            CreatorId = "c1",
            Creator = new ApplicationUser { DisplayName = null, Email = "artist@test.com" }
        };
        _tracks.GetByIdAsync(id).Returns(track);

        var result = await _sut.GetTrackAsync(id.ToString());

        Assert.Equal("artist@test.com", result!.Artist);
    }

    [Fact]
    public async Task GetCatalogAsync_ReturnsMappedTracks()
    {
        var tracks = new List<Track>
        {
            new() { Id = Guid.NewGuid(), Title = "A", Price = 5, CreatorId = "c1", Creator = new ApplicationUser { DisplayName = "Art" } },
            new() { Id = Guid.NewGuid(), Title = "B", Price = 10, CreatorId = "c2", Creator = new ApplicationUser { DisplayName = "Bee" } }
        };
        _tracks.BrowseAsync(1, 50, null, null, null, null, null, null, null).Returns(tracks);

        var result = await _sut.GetCatalogAsync();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Title == "A");
        Assert.Contains(result, r => r.Title == "B");
    }

    [Fact]
    public async Task GetTrackAsync_ZeroPriceCents_MapsToZeroDecimal()
    {
        var id = Guid.NewGuid();
        var track = new Track
        {
            Id = id,
            Title = "Free Beat",
            Price = 0,
            NonExclusivePriceCents = 0,
            ExclusivePriceCents = 0,
            CreatorId = "c1",
            Creator = new ApplicationUser { DisplayName = "X" }
        };
        _tracks.GetByIdAsync(id).Returns(track);

        var result = await _sut.GetTrackAsync(id.ToString());

        Assert.Equal(0m, result!.Price);
        Assert.Equal(0m, result.NonExclusivePrice);
        Assert.Equal(0m, result.ExclusivePrice);
    }
}
