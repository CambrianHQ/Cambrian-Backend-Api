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
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public CatalogController(ICatalogService catalog, IObjectStorage storage, IMemoryCache cache)
    {
        _catalog = catalog;
        _storage = storage;
        _cache = cache;
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
        return Ok(ToPaginatedEnvelope(result));
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? genre = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sort = null,
        [FromQuery] string? mood = null,
        [FromQuery] string? tempo = null,
        [FromQuery] bool? instrumental = null,
        [FromQuery] string? duration = null)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 50;
        var cacheKey = $"catalog:paged:{page}:{pageSize}:{genre}:{search}:{sort}:{mood}:{tempo}:{instrumental}:{duration}";
        var result = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await _catalog.GetCatalogPagedAsync(page, pageSize, genre, search, sort, mood, tempo, instrumental, duration);
        });
        ResolveTrackUrls(result!.Items);
        return Ok(ToPaginatedEnvelope(result));
    }

    /// <summary>
    /// Backward-compatible paginated envelope: "data" remains the track array
    /// so existing clients are unaffected, and pagination metadata is added as
    /// sibling fields.
    /// </summary>
    private static object ToPaginatedEnvelope<T>(Cambrian.Application.DTOs.Catalog.PagedResult<T> paged) => new
    {
        success = true,
        data = paged.Items,
        page = paged.Page,
        pageSize = paged.PageSize,
        totalCount = paged.TotalCount,
        totalPages = paged.TotalPages,
        hasNextPage = paged.HasNextPage,
        hasPreviousPage = paged.HasPreviousPage
    };

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
            result.CoverArtUrl = ResolveCoverArtUrl(result.CoverArtUrl);
        return OkResponse(result);
    }

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
                t.CoverArtUrl = ResolveCoverArtUrl(t.CoverArtUrl);
        }
    }

    /// <summary>
    /// Produce a public URL for cover art that works for both local storage
    /// (relative /uploads/ path) and S3/R2 (public bucket URL or signed URL).
    /// </summary>
    private string ResolveCoverArtUrl(string rawUrl)
    {
        // If it's already an absolute URL (e.g. from S3), leave it alone
        if (rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return rawUrl;
        // For local storage paths (/uploads/covers/...) — keep using the existing absolute URL resolution
        if (rawUrl.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
            return ResolveAbsoluteUrl(rawUrl);
        // Otherwise it's an object key (e.g. covers/{creatorId}/{guid}.jpg) — use storage provider
        return _storage.GetPublicUrl(rawUrl);
    }
}