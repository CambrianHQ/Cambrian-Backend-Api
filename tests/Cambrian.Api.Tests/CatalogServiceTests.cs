using Cambrian.Application.Services;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
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
        users.Users.Returns(new List<ApplicationUser>().AsQueryable());
        var profiles = Substitute.For<ICreatorProfileRepository>();
        profiles.GetSlugsByUserIdsAsync(Arg.Any<List<string>>())
            .Returns(new Dictionary<string, (string? Slug, string? ProfileImageUrl)>());
        var logger = Substitute.For<ILogger<CatalogService>>();
        _sut = new CatalogService(_tracks, users, profiles, logger);
    }

    [Fact]
    public async Task GetTrackAsync_ReturnsNull_WhenCambrianTrackIdNotFound()
    {
        _tracks.GetByCambrianTrackIdAsync("not-a-guid").Returns((Track?)null);

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
    public async Task GetTrackAsync_LooksUpCambrianTrackId()
    {
        var id = Guid.NewGuid();
        var cambrianTrackId = "CAMB-TRK-ABC12345";
        var track = new Track
        {
            Id = id,
            CambrianTrackId = cambrianTrackId,
            Title = "Frontend Route Beat",
            Price = 19.99m,
            LicenseType = "non-exclusive",
            Visibility = "public"
        };
        _tracks.GetByCambrianTrackIdAsync(cambrianTrackId).Returns(track);

        var result = await _sut.GetTrackAsync(cambrianTrackId);

        Assert.NotNull(result);
        Assert.Equal(id.ToString(), result!.Id);
        Assert.Equal(cambrianTrackId, result.CambrianTrackId);
        Assert.Equal("Frontend Route Beat", result.Title);
    }

    [Fact]
    public async Task GetTrackAsync_CentsPricing_UsesNonExclusivePriceForLegacyAlias()
    {
        var id = Guid.NewGuid();
        var track = new Track
        {
            Id = id,
            Title = "Beat 3",
            Price = 0m,
            NonExclusivePriceCents = 1999,
            ExclusivePriceCents = 4999,
            CopyrightBuyoutPriceCents = 9999,
            CreatorId = "creator-1",
            Creator = new ApplicationUser { DisplayName = "DJ Test" }
        };
        _tracks.GetByIdAsync(id).Returns(track);

        var result = await _sut.GetTrackAsync(id.ToString());

        Assert.NotNull(result);
        Assert.Equal(19.99m, result!.Price);
        Assert.Equal(19.99m, result.NonExclusivePrice);
        Assert.Equal(49.99m, result.ExclusivePrice);
        Assert.Equal(99.99m, result.CopyrightBuyoutPrice);
    }

    [Fact]
    public async Task GetTrackAsync_FallsBackToUnknown_WhenDisplayNameIsNull()
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

        Assert.Equal("Unknown Artist", result!.Artist);
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
