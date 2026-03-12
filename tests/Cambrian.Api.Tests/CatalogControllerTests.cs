using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
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
    private readonly CatalogController _controller;

    public CatalogControllerTests()
    {
        _controller = new CatalogController(_catalog, _storage, _cache);
    }

    // ── Discover ──

    [Fact]
    public async Task Discover_ReturnsOk_WithTracks()
    {
        _catalog.GetDiscoverAsync(1, 20, null, null).Returns(new List<TrackResponse>
        {
            new() { Id = Guid.NewGuid().ToString(), Title = "Beat 1" }
        });

        var result = await _controller.Discover();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<ApiResponse<IReadOnlyCollection<TrackResponse>>>(ok.Value);
    }

    [Fact]
    public async Task Discover_ClampsPageToMinimumOne()
    {
        _catalog.GetDiscoverAsync(1, 20, null, null).Returns(new List<TrackResponse>());

        await _controller.Discover(page: -5);

        await _catalog.Received(1).GetDiscoverAsync(1, 20, null, null);
    }

    [Fact]
    public async Task Discover_ClampsPageSizeTo20_WhenOutOfRange()
    {
        _catalog.GetDiscoverAsync(1, 20, null, null).Returns(new List<TrackResponse>());

        await _controller.Discover(pageSize: 999);

        await _catalog.Received(1).GetDiscoverAsync(1, 20, null, null);
    }

    [Fact]
    public async Task Discover_PassesGenreAndSearch()
    {
        _catalog.GetDiscoverAsync(1, 20, "hip-hop", "fire").Returns(new List<TrackResponse>());

        await _controller.Discover(genre: "hip-hop", search: "fire");

        await _catalog.Received(1).GetDiscoverAsync(1, 20, "hip-hop", "fire");
    }

    // ── Catalog ──

    [Fact]
    public async Task Catalog_ClampsPageSizeTo50_WhenZero()
    {
        _catalog.GetCatalogAsync(1, 50, null, null, null).Returns(new List<TrackResponse>());

        await _controller.Catalog(pageSize: 0);

        await _catalog.Received(1).GetCatalogAsync(1, 50, null, null, null);
    }

    [Fact]
    public async Task Catalog_PassesSortParameter()
    {
        _catalog.GetCatalogAsync(1, 50, null, null, "newest").Returns(new List<TrackResponse>());

        await _controller.Catalog(sort: "newest");

        await _catalog.Received(1).GetCatalogAsync(1, 50, null, null, "newest");
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
        _catalog.GetDiscoverAsync(1, 20, null).Returns(new List<TrackResponse>());

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
