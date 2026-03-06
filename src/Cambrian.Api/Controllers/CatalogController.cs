using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("")]
public class CatalogController : BaseController
{
    private readonly ICatalogService _catalog;

    public CatalogController(ICatalogService catalog)
    {
        _catalog = catalog;
    }

    [HttpGet("discover")]
    public async Task<IActionResult> Discover(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? genre = null,
        [FromQuery] string? search = null)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;
        return OkResponse(await _catalog.GetDiscoverAsync(page, pageSize, genre, search));
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? genre = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sort = null)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 50;
        return OkResponse(await _catalog.GetCatalogAsync(page, pageSize, genre, search, sort));
    }

    [HttpGet("tracks/{trackId}")]
    public async Task<IActionResult> GetTrack(string trackId)
    {
        if (!Guid.TryParse(trackId, out _))
            return ErrorResponse("trackId must be a valid GUID.");

        var result = await _catalog.GetTrackAsync(trackId);
        return result is null
            ? NotFoundResponse($"Track '{trackId}' not found.")
            : OkResponse(result);
    }

    [HttpGet("trending")]
    public async Task<IActionResult> Trending(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? genre = null)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;
        return OkResponse(await _catalog.GetDiscoverAsync(page, pageSize, genre));
    }

    [HttpGet("tracks")]
    public async Task<IActionResult> ListTracks()
    {
        return OkResponse(await _catalog.GetCatalogAsync());
    }

    [Authorize(Roles = "Creator")]
    [HttpPost("tracks/upload")]
    public IActionResult TracksUpload()
    {
        return CreatedResponse<object?>(null, "Track upload initiated.");
    }
}