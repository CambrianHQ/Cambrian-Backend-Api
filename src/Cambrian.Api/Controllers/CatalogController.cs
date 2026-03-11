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
        var items = await _catalog.GetDiscoverAsync(page, pageSize, genre, search);
        ResolveTrackUrls(items);
        return OkResponse(items);
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
        var items = await _catalog.GetCatalogAsync(page, pageSize, genre, search, sort);
        ResolveTrackUrls(items);
        return OkResponse(items);
    }

    [HttpGet("tracks/{trackId}")]
    public async Task<IActionResult> GetTrack(string trackId)
    {
        if (!Guid.TryParse(trackId, out _))
            return ErrorResponse("trackId must be a valid GUID.");

        var result = await _catalog.GetTrackAsync(trackId);
        if (result is null)
            return NotFoundResponse($"Track '{trackId}' not found.");
        result.AudioUrl = ResolveAbsoluteUrl($"/stream/{result.Id}/audio");
        if (!string.IsNullOrEmpty(result.CoverArtUrl))
            result.CoverArtUrl = ResolveAbsoluteUrl(result.CoverArtUrl);
        return OkResponse(result);
    }

    [HttpGet("trending")]
    public async Task<IActionResult> Trending(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? genre = null)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;
        var items = await _catalog.GetDiscoverAsync(page, pageSize, genre);
        ResolveTrackUrls(items);
        return OkResponse(items);
    }

    [HttpGet("tracks")]
    public async Task<IActionResult> ListTracks()
    {
        var items = await _catalog.GetCatalogAsync();
        ResolveTrackUrls(items);
        return OkResponse(items);
    }

    private void ResolveTrackUrls(IEnumerable<Cambrian.Application.DTOs.Catalog.TrackResponse> tracks)
    {
        foreach (var t in tracks)
        {
            // Point audioUrl at the authenticated streaming proxy so browsers
            // get the correct Content-Type, Range support, and CORS headers.
            t.AudioUrl = ResolveAbsoluteUrl($"/stream/{t.Id}/audio");
            if (!string.IsNullOrEmpty(t.CoverArtUrl))
                t.CoverArtUrl = ResolveAbsoluteUrl(t.CoverArtUrl);
        }
    }

    [Authorize(Roles = "Creator")]
    [HttpPost("tracks/upload")]
    public IActionResult TracksUpload()
    {
        return CreatedResponse<object?>(null, "Track upload initiated.");
    }
}