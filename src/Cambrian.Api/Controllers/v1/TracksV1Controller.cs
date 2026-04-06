using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers.v1;

/// <summary>
/// Public versioned API — track catalogue.
/// Supports both anonymous access and authentication via JWT Bearer or X-API-Key header.
/// </summary>
[ApiController]
[Route("api/v1")]
public class TracksV1Controller : ControllerBase
{
    private readonly ICatalogService _catalog;

    public TracksV1Controller(ICatalogService catalog) => _catalog = catalog;

    /// <summary>
    /// Search and filter the public track catalogue.
    /// </summary>
    [HttpGet("tracks")]
    [AllowAnonymous]
    public async Task<IActionResult> SearchTracks(
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
        page  = Math.Max(page, 1);

        var result = await _catalog.GetCatalogPagedAsync(
            page:        page,
            pageSize:    limit,
            genre:       genre,
            search:      search,
            sort:        sort,
            mood:        mood,
            tempo:       tempo,
            instrumental: instrumental,
            duration:    null);

        return Ok(new
        {
            success = true,
            data    = result.Items,
            meta    = new
            {
                page       = result.Page,
                limit      = result.PageSize,
                total      = result.TotalCount,
                totalPages = result.TotalPages,
                hasNext    = result.HasNextPage,
                hasPrev    = result.HasPreviousPage,
            },
        });
    }

    /// <summary>Get a single track by its Cambrian track ID (CAMB-TRK-XXXX) or UUID.</summary>
    [HttpGet("tracks/{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetTrack(string id)
    {
        var track = await _catalog.GetTrackAsync(id);
        if (track is null)
            return NotFound(new { success = false, error = "Track not found." });

        return Ok(new { success = true, data = track });
    }

    /// <summary>List all genres present in the catalogue with track counts.</summary>
    [HttpGet("genres")]
    [AllowAnonymous]
    public async Task<IActionResult> GetGenres()
    {
        // Fetch a broad set and aggregate — a dedicated service method can be added
        // when the catalogue grows large enough to warrant it.
        var result = await _catalog.GetCatalogPagedAsync(
            page: 1, pageSize: 1000,
            genre: null, search: null, sort: null,
            mood: null, tempo: null, instrumental: null, duration: null);

        var genres = result.Items
            .Where(t => !string.IsNullOrEmpty(t.Genre))
            .GroupBy(t => t.Genre!)
            .Select(g => new { genre = g.Key, count = g.Count() })
            .OrderByDescending(g => g.count)
            .ToList();

        return Ok(new { success = true, data = genres });
    }
}
