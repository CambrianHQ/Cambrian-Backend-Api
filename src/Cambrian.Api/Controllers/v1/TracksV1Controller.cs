using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.V1;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cambrian.Api.Controllers.v1;

/// <summary>
/// Public versioned API — track catalogue.
/// Authenticates via X-API-Key (preferred for server-to-server) or JWT bearer
/// (preferred for first-party UI). Rate-limited per key/IP via the
/// "api_key_free" policy (100 req/min). Every call is recorded as an
/// "api_call" analytics event by <see cref="ApiUsageActionFilter"/>.
/// </summary>
[ApiController]
[Route("api/v1")]
[EnableRateLimiting("api_key_free")]
[ServiceFilter(typeof(ApiUsageActionFilter))]
public class TracksV1Controller : ControllerBase
{
    private readonly ICatalogService _catalog;

    public TracksV1Controller(ICatalogService catalog) => _catalog = catalog;

    /// <summary>
    /// Search and filter the public track catalogue.
    /// Canonical route. <c>/api/v1/tracks</c> is kept as a backward-compatible alias.
    /// </summary>
    [HttpGet("tracks/search")]
    [HttpGet("tracks")]
    [AllowAnonymous]
    public async Task<ActionResult<V1ApiResponse<IEnumerable<object>>>> SearchTracks(
        [FromQuery] string? genre,
        [FromQuery] string? mood,
        [FromQuery] string? search,
        [FromQuery] string? tempo,
        [FromQuery] bool? instrumental,
        [FromQuery] string? sort,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        page = Math.Max(page, 1);

        var result = await _catalog.GetCatalogPagedAsync(
            page: page,
            pageSize: limit,
            genre: genre,
            search: search,
            sort: sort,
            mood: mood,
            tempo: tempo,
            instrumental: instrumental,
            duration: null);

        var meta = new V1PaginationMeta
        {
            Page = result.Page,
            Limit = result.PageSize,
            Total = result.TotalCount,
            TotalPages = result.TotalPages,
            HasNext = result.HasNextPage,
            HasPrev = result.HasPreviousPage,
        };

        return Ok(V1ApiResponse<IEnumerable<object>>.Ok(result.Items.Cast<object>(), meta));
    }

    /// <summary>Get a single track by its Cambrian track ID (CAMB-TRK-XXXX) or UUID.</summary>
    [HttpGet("tracks/{id}")]
    [AllowAnonymous]
    public async Task<ActionResult<V1ApiResponse<object>>> GetTrack(string id)
    {
        var track = await _catalog.GetTrackAsync(id);
        if (track is null)
            return NotFound(V1ApiResponse<object>.Fail("Track not found."));

        return Ok(V1ApiResponse<object>.Ok((object)track));
    }

    /// <summary>List all genres present in the catalogue with track counts.</summary>
    [HttpGet("genres")]
    [AllowAnonymous]
    public async Task<ActionResult<V1ApiResponse<IEnumerable<object>>>> GetGenres()
    {
        var result = await _catalog.GetCatalogPagedAsync(
            page: 1, pageSize: 1000,
            genre: null, search: null, sort: null,
            mood: null, tempo: null, instrumental: null, duration: null);

        var genres = result.Items
            .Where(t => !string.IsNullOrEmpty(t.Genre))
            .GroupBy(t => t.Genre!)
            .Select(g => (object)new { genre = g.Key, count = g.Count() })
            .OrderByDescending(g => ((dynamic)g).count)
            .ToList();

        return Ok(V1ApiResponse<IEnumerable<object>>.Ok(genres));
    }
}
