using System.Security.Claims;
using Cambrian.Api.Middleware;
using Cambrian.Api.Security;
using Cambrian.Application.DTOs.Catalog;
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
[AllowApiKey]
public class TracksV1Controller : ControllerBase
{
    private readonly ICatalogService _catalog;
    private readonly ITrackVisibilityPolicy _visibility;
    private readonly ITrackDetailsRepository _trackDetails;
    private readonly ITrackRepository _tracks;
    private readonly ICreatorIdentityRepository _creators;

    public TracksV1Controller(
        ICatalogService catalog,
        ITrackVisibilityPolicy visibility,
        ITrackDetailsRepository trackDetails,
        ITrackRepository tracks,
        ICreatorIdentityRepository creators)
    {
        _catalog = catalog;
        _visibility = visibility;
        _trackDetails = trackDetails;
        _tracks = tracks;
        _creators = creators;
    }

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

    /// <summary>
    /// Get lyrics for a track. 404 when the track doesn't exist, isn't visible
    /// to the requester, or has no lyrics — never distinguishes those cases so
    /// draft/private tracks can't be enumerated.
    /// </summary>
    [HttpGet("tracks/{trackId}/lyrics")]
    [AllowAnonymous]
    public async Task<ActionResult<V1ApiResponse<object>>> GetTrackLyrics(string trackId)
    {
        var track = await ResolveVisibleTrackAsync(trackId);
        if (track is null)
            return NotFound(V1ApiResponse<object>.Fail("Track not found."));

        var lyrics = await _trackDetails.GetLyricsAsync(Guid.Parse(track.Id));
        if (lyrics is null)
            return NotFound(V1ApiResponse<object>.Fail("This track has no lyrics."));

        return Ok(V1ApiResponse<object>.Ok((object)lyrics));
    }

    /// <summary>
    /// Create or update lyrics for a track. Creator-owned; never touches the
    /// Track row itself, so engagement (plays/sales) is unaffected. Sending
    /// empty/whitespace lyrics removes the row (lyrics are optional).
    /// </summary>
    [Authorize(Policy = "CanEditOwnTrack")]
    [HttpPut("tracks/{trackId:guid}/lyrics")]
    public async Task<ActionResult<V1ApiResponse<object>>> UpsertTrackLyrics(Guid trackId, [FromBody] UpsertTrackLyricsRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(V1ApiResponse<object>.Fail("Authentication required."));

        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null)
            return NotFound(V1ApiResponse<object>.Fail("Track not found."));

        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var ownsLegacy = track.CreatorId == userId;
        var ownsUuid = creatorUuid.HasValue && track.CreatorUuid == creatorUuid.Value;
        if (!ownsLegacy && !ownsUuid)
            return StatusCode(403, V1ApiResponse<object>.Fail("You can only edit your own tracks."));

        var lyrics = request.Lyrics?.Trim() ?? "";
        if (lyrics.Length == 0)
        {
            await _trackDetails.DeleteLyricsAsync(trackId);
            return Ok(V1ApiResponse<object>.Ok(null!));
        }

        var language = NormalizeLanguageTag(request.Language);
        if (language is null)
            return BadRequest(V1ApiResponse<object>.Fail("Language must be a valid language tag (e.g. 'en', 'pt-BR')."));

        var saved = await _trackDetails.UpsertLyricsAsync(trackId, lyrics, language, request.IsExplicit);
        return Ok(V1ApiResponse<object>.Ok((object)saved));
    }

    /// <summary>Resolves a track and applies the shared visibility policy (C4).</summary>
    private async Task<TrackResponse?> ResolveVisibleTrackAsync(string trackId)
    {
        var track = await _catalog.GetTrackAsync(trackId);
        if (track is null) return null;
        var requesterId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var isAdmin = User?.IsInRole("Admin") == true;
        return _visibility.CanAccess(track.Visibility ?? "public", track.CreatorId, requesterId, isAdmin) ? track : null;
    }

    /// <summary>Accepts short BCP-47-ish tags: "en", "pt-BR", "zh-Hans". Null/empty → "en".</summary>
    private static string? NormalizeLanguageTag(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return "en";
        if (value.Length > 16) return null;
        return value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? value : null;
    }
}
