using System.Security.Claims;
using Cambrian.Api.Contracts.Catalog;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Cambrian.Api.Controllers;

[Route("")]
public class CatalogController : BaseController
{
    private readonly ICatalogService _catalog;
    private readonly IObjectStorage _storage;
    private readonly IMemoryCache _cache;
    private readonly ITrackVisibilityPolicy _visibility;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly IActivityService _activity;

    public CatalogController(ICatalogService catalog, IObjectStorage storage, IMemoryCache cache, IActivityService activity, ITrackVisibilityPolicy visibility)
    {
        _catalog = catalog;
        _storage = storage;
        _cache = cache;
        _activity = activity;
        _visibility = visibility;
    }

    [HttpGet("discover")]
    public async Task<IActionResult> Discover(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? genre = null,
        [FromQuery] string? search = null,
        [FromQuery] string? mood = null,
        [FromQuery] string? tempo = null,
        [FromQuery] bool? instrumental = null,
        [FromQuery] string? duration = null)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;
        var cacheKey = $"discover:paged:{page}:{pageSize}:{genre}:{search}:{mood}:{tempo}:{instrumental}:{duration}";
        var result = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await _catalog.GetDiscoverPagedAsync(page, pageSize, genre, search, mood, tempo, instrumental, duration);
        });
        ResolveTrackUrls(result!.Items);
        return Ok(ToPublicEnvelope(result));
    }

    [HttpGet("catalog")]
    [ProducesResponseType(typeof(PublicCatalogPageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PublicCatalogPageResponse>> Catalog([FromQuery] CatalogQueryRequest query)
    {
        // Frontend infinite-scroll (offset+limit) takes precedence over legacy page/pageSize.
        // Offset is page-aligned (offset = pageIndex × limit) — exactly how infinite scroll
        // requests pages — and limit caps at 60. Legacy page/pageSize keeps its existing 100 cap.
        int page, pageSize;
        if (query.Offset.HasValue || query.Limit.HasValue)
        {
            pageSize = Math.Clamp(query.Limit ?? 60, 1, 60);
            page = (Math.Max(0, query.Offset ?? 0) / pageSize) + 1;
        }
        else
        {
            page = Math.Max(query.Page, 1);
            pageSize = query.PageSize is < 1 or > 100 ? 50 : query.PageSize;
        }

        var cacheKey = $"catalog:paged:{page}:{pageSize}:{query.Genre}:{query.Search}:{query.Sort}:{query.Mood}:{query.Tempo}:{query.Instrumental}:{query.Duration}";
        var result = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await _catalog.GetCatalogPagedAsync(
                page,
                pageSize,
                query.Genre,
                query.Search,
                query.Sort,
                query.Mood,
                query.Tempo,
                query.Instrumental,
                query.Duration);
        });
        ResolveTrackUrls(result!.Items);
        return Ok(ToPublicEnvelope(result));
    }

    /// <summary>
    /// Anonymous-safe paginated envelope. "data" remains the track array so
    /// existing clients are unaffected, but each item is projected through
    /// <see cref="PublicTrackDto"/> (no platform fee / creator earnings — F18) and
    /// total/offset/limit are added for the frontend infinite-scroll contract.
    /// </summary>
    private static PublicCatalogPageResponse ToPublicEnvelope(PagedResult<TrackResponse> paged) => new()
    {
        Data = paged.Items.Select(PublicCatalogTrackDto.From).ToList(),
        Page = paged.Page,
        PageSize = paged.PageSize,
        Offset = (paged.Page - 1) * paged.PageSize,
        Limit = paged.PageSize,
        Total = paged.TotalCount,
        TotalCount = paged.TotalCount,
        TotalPages = paged.TotalPages,
        HasNextPage = paged.HasNextPage,
        HasPreviousPage = paged.HasPreviousPage
    };

    [HttpGet("tracks/{trackId}")]
    [HttpGet("track/{trackId}")]
    public async Task<IActionResult> GetTrack(string trackId)
    {
        var result = await _catalog.GetTrackAsync(trackId);
        if (result is null)
            return NotFoundResponse($"Track '{trackId}' not found.");

        // C4: enforce visibility via shared policy — single source of truth.
        var visUserId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!_visibility.CanAccess(result.Visibility ?? "public", result.CreatorId, visUserId, User?.IsInRole("Admin") == true))
            return NotFoundResponse($"Track '{trackId}' not found.");

        result.AudioUrl = ResolveAbsoluteUrl($"/stream/{result.Id}/audio");
        if (!string.IsNullOrEmpty(result.CoverArtUrl))
            result.CoverArtUrl = ResolveCoverArtUrl(result.CoverArtUrl);
        // Project to the anonymous-safe DTO — this route is public (F18).
        return OkResponse(PublicCatalogTrackDto.From(result));
    }

    // Frontend "checkout restore" hits /catalog/{id} expecting a single track. The
    // canonical route is /tracks/{id} above; this is an alias so the existing
    // frontend deploy stops 404'ing while it's updated.
    [HttpGet("catalog/{trackId}")]
    public Task<IActionResult> GetTrackCatalogAlias(string trackId) => GetTrack(trackId);

    [HttpGet("trending")]
    public async Task<IActionResult> Trending(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? genre = null,
        [FromQuery] string? mood = null,
        [FromQuery] string? tempo = null,
        [FromQuery] bool? instrumental = null,
        [FromQuery] string? duration = null)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;
        var items = await _catalog.GetDiscoverAsync(page, pageSize, genre, null, mood, tempo, instrumental, duration);
        ResolveTrackUrls(items);
        return OkResponse(items.Select(PublicCatalogTrackDto.From).ToList());
    }

    [HttpGet("tracks")]
    public async Task<IActionResult> ListTracks()
    {
        var items = await _catalog.GetCatalogAsync();
        ResolveTrackUrls(items);
        return OkResponse(items.Select(PublicCatalogTrackDto.From).ToList());
    }

    [HttpGet("tracks/trending")]
    public async Task<IActionResult> TracksTrending([FromQuery] int limit = 12, CancellationToken ct = default)
    {
        var items = await _activity.GetTrendingAsync(ct);
        if (items.Count > limit)
            items = items.Take(limit).ToList();
        return OkResponse(items);
    }

    private void ResolveTrackUrls(IEnumerable<TrackResponse> tracks)
    {
        foreach (var t in tracks)
        {
            // Point audioUrl at the authenticated streaming proxy so browsers
            // get the correct Content-Type, Range support, and CORS headers.
            t.AudioUrl = ResolveAbsoluteUrl($"/stream/{t.Id}/audio");
            if (!string.IsNullOrEmpty(t.CoverArtUrl))
                t.CoverArtUrl = ResolveImageUrl(t.CoverArtUrl);
            if (!string.IsNullOrEmpty(t.CreatorProfileImageUrl))
                t.CreatorProfileImageUrl = ResolveImageUrl(t.CreatorProfileImageUrl);
        }
    }

    /// <summary>
    /// Produce a public URL for cover art that works for both local storage
    /// (relative /uploads/ path) and S3/R2 (proxied through /images/ endpoint).
    /// </summary>
    private string ResolveCoverArtUrl(string rawUrl) => ResolveImageUrl(rawUrl);
}
