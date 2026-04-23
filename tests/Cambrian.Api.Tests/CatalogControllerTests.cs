using System.Net.Http.Json;
using Cambrian.Api.Common;
using Cambrian.Api.Contracts.Catalog;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Controller-level tests for CatalogController covering discover, catalog,
/// individual track retrieval, and trending endpoints. Verifies parameter
/// clamping, GUID validation, and 404 responses for missing tracks.
/// </summary>
public sealed class CatalogControllerTests
{
    private readonly ICatalogService _catalog = Substitute.For<ICatalogService>();
    private readonly IObjectStorage _storage = Substitute.For<IObjectStorage>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IActivityService _activity = Substitute.For<IActivityService>();
    private readonly CatalogController _controller;

    public CatalogControllerTests()
    {
        _controller = new CatalogController(_catalog, _storage, _cache, _activity, new TrackVisibilityPolicy());
    }

    // ── Discover ──

    private static PagedResult<TrackResponse> EmptyPaged(int page = 1, int pageSize = 20) => new()
    {
        Items = new List<TrackResponse>(),
        Page = page,
        PageSize = pageSize,
        TotalCount = 0
    };

    [Fact]
    public async Task Discover_ReturnsOk_WithTracks()
    {
        _catalog.GetDiscoverPagedAsync(1, 20, null, null, null, null, null, null).Returns(new PagedResult<TrackResponse>
        {
            Items = new List<TrackResponse> { new() { Id = Guid.NewGuid().ToString(), Title = "Beat 1" } },
            Page = 1,
            PageSize = 20,
            TotalCount = 1
        });

        var result = await _controller.Discover();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Discover_ClampsPageToMinimumOne()
    {
        _catalog.GetDiscoverPagedAsync(1, 20, null, null, null, null, null, null).Returns(EmptyPaged());

        await _controller.Discover(page: -5);

        await _catalog.Received(1).GetDiscoverPagedAsync(1, 20, null, null, null, null, null, null);
    }

    [Fact]
    public async Task Discover_ClampsPageSizeTo20_WhenOutOfRange()
    {
        _catalog.GetDiscoverPagedAsync(1, 20, null, null, null, null, null, null).Returns(EmptyPaged());

        await _controller.Discover(pageSize: 999);

        await _catalog.Received(1).GetDiscoverPagedAsync(1, 20, null, null, null, null, null, null);
    }

    [Fact]
    public async Task Discover_PassesGenreAndSearch()
    {
        _catalog.GetDiscoverPagedAsync(1, 20, "hip-hop", "fire", null, null, null, null).Returns(EmptyPaged());

        await _controller.Discover(genre: "hip-hop", search: "fire");

        await _catalog.Received(1).GetDiscoverPagedAsync(1, 20, "hip-hop", "fire", null, null, null, null);
    }

    // ── Catalog ──

    [Fact]
    public async Task Catalog_ClampsPageSizeTo50_WhenZero()
    {
        _catalog.GetCatalogPagedAsync(1, 50, null, null, null, null, null, null, null).Returns(EmptyPaged(1, 50));

        await _controller.Catalog(new CatalogQueryRequest { PageSize = 0 });

        await _catalog.Received(1).GetCatalogPagedAsync(1, 50, null, null, null, null, null, null, null);
    }

    [Fact]
    public async Task Catalog_PassesSortParameter()
    {
        _catalog.GetCatalogPagedAsync(1, 50, null, null, "newest", null, null, null, null).Returns(EmptyPaged(1, 50));

        await _controller.Catalog(new CatalogQueryRequest { Sort = "newest" });

        await _catalog.Received(1).GetCatalogPagedAsync(1, 50, null, null, "newest", null, null, null, null);
    }

    [Fact]
    public async Task Catalog_ReturnsTypedPaginatedEnvelope()
    {
        _catalog.GetCatalogPagedAsync(1, 50, null, null, null, null, null, null, null).Returns(new PagedResult<TrackResponse>
        {
            Items = new List<TrackResponse> { new() { Id = Guid.NewGuid().ToString(), Title = "Beat 1" } },
            Page = 1,
            PageSize = 50,
            TotalCount = 1
        });

        var result = await _controller.Catalog(new CatalogQueryRequest());

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var envelope = Assert.IsType<CatalogPageResponse>(ok.Value);
        Assert.True(envelope.Success);
        Assert.Single(envelope.Data);
        Assert.Equal(50, envelope.PageSize);
    }

    [Fact]
    public async Task Catalog_NormalizesAbsoluteBackendImageUrls_WithoutDoubleProxying()
    {
        var trackId = Guid.NewGuid().ToString();
        _catalog.GetCatalogPagedAsync(1, 50, null, null, null, null, null, null, null).Returns(new PagedResult<TrackResponse>
        {
            Items = new List<TrackResponse>
            {
                new()
                {
                    Id = trackId,
                    Title = "Beat 1",
                    CoverArtUrl = "https://old-api.example.com/images/covers/user-1/beat.jpg"
                }
            },
            Page = 1,
            PageSize = 50,
            TotalCount = 1
        });

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        _controller.ControllerContext.HttpContext.Request.Scheme = "https";
        _controller.ControllerContext.HttpContext.Request.Host = new HostString("api.example.com");

        var result = await _controller.Catalog(new CatalogQueryRequest());

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var envelope = Assert.IsType<CatalogPageResponse>(ok.Value);
        var item = Assert.Single(envelope.Data);
        Assert.Equal("https://api.example.com/images/covers/user-1/beat.jpg", item.CoverArtUrl);
        Assert.DoesNotContain("/images/images/", item.CoverArtUrl, StringComparison.OrdinalIgnoreCase);
    }

    // ── GetTrack ──

    [Fact]
    public async Task GetTrack_Returns400_WhenTrackIdNotGuid()
    {
        var result = await _controller.GetTrack("not-a-guid");

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(bad.Value);
        Assert.False(envelope.Success);
        Assert.Contains("GUID", envelope.Error);
    }

    [Fact]
    public async Task GetTrack_Returns404_WhenTrackNotFound()
    {
        var trackId = Guid.NewGuid().ToString();
        _catalog.GetTrackAsync(trackId).Returns((TrackResponse?)null);

        var result = await _controller.GetTrack(trackId);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(notFound.Value);
        Assert.False(envelope.Success);
        Assert.Contains("not found", envelope.Error);
    }

    [Fact]
    public async Task GetTrack_Returns200_WhenFound()
    {
        var trackId = Guid.NewGuid().ToString();
        _catalog.GetTrackAsync(trackId).Returns(new TrackResponse
        {
            Id = trackId,
            Title = "My Beat"
        });

        var result = await _controller.GetTrack(trackId);

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Trending ──

    [Fact]
    public async Task Trending_DelegatesCorrectly()
    {
        _catalog.GetDiscoverAsync(1, 20, null, null, null, null, null, null).Returns(new List<TrackResponse>());

        var result = await _controller.Trending();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── ListTracks ──

    [Fact]
    public async Task ListTracks_ReturnsOk()
    {
        _catalog.GetCatalogAsync().Returns(new List<TrackResponse>());

        var result = await _controller.ListTracks();

        Assert.IsType<OkObjectResult>(result);
    }
}

/// <summary>
/// Integration test: catalog search must match on Tags, not just title/genre/mood.
/// Regression test for BUG-S4-03.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogSearchIntegrationTests : IClassFixture<Cambrian.Api.Tests.Fixtures.CambrianApiFixture>
{
    private readonly Cambrian.Api.Tests.Fixtures.CambrianApiFixture _fixture;

    public CatalogSearchIntegrationTests(Cambrian.Api.Tests.Fixtures.CambrianApiFixture fixture)
        => _fixture = fixture;

    [Fact]
    public async Task Catalog_Search_Matches_On_Tags()
    {
        // Seed a creator
        var email = $"tagsearch-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        var creatorId = await _fixture.GetUserIdAsync(email);
        await _fixture.SetUserRoleAsync(email, "Creator");
        await _fixture.SetUsernameAsync(email, $"ts{Guid.NewGuid():N}"[..12]);

        // Seed a track with tags but a title that does NOT match the search term
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Cambrian.Persistence.CambrianDbContext>();
        var trackId = Guid.NewGuid();
        db.Tracks.Add(new Cambrian.Domain.Entities.Track
        {
            Id = trackId,
            CambrianTrackId = $"CAMB-TRK-{trackId.ToString()[..8].ToUpper()}",
            Title = "Untitled",
            Price = 9.99m,
            LicenseType = "standard",
            AudioUrl = "tracks/tag-test.mp3",
            CreatorId = creatorId,
            Genre = "Electronic",
            Visibility = "public",
            Tags = new List<string> { "lofi", "chill" }
        });
        await db.SaveChangesAsync();

        // Search for "lofi" — should match via Tags
        using var client = _fixture.CreateClient();
        var response = await client.GetAsync("/catalog?search=lofi");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected 2xx but got {(int)response.StatusCode}: {body}");

        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var data = json.GetProperty("data");

        // The track must appear in results
        var found = false;
        foreach (var item in data.EnumerateArray())
        {
            var idStr = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (idStr != null && Guid.TryParse(idStr, out var g) && g == trackId)
            {
                found = true;
                break;
            }
        }

        Assert.True(found, $"Track {trackId} with Tags=[lofi,chill] was not returned by /catalog?search=lofi");
    }
}
