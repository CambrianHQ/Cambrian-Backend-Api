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
    private readonly ITrackDetailsRepository _trackDetails;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);

    private readonly IActivityService _activity;

    public CatalogController(ICatalogService catalog, IObjectStorage storage, IMemoryCache cache, IActivityService activity, ITrackVisibilityPolicy visibility, ITrackDetailsRepository trackDetails)
    {
        _catalog = catalog;
        _storage = storage;
        _cache = cache;
        _activity = activity;
        _visibility = visibility;
        _trackDetails = trackDetails;
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
        return Ok(ToPublicEnvelope(result!));
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
        return Ok(ToPublicEnvelope(result!));
    }

    /// <summary>
    /// Anonymous-safe paginated envelope. "data" remains the track array so
    /// existing clients are unaffected, but each item is projected through
    /// <see cref="PublicTrackDto"/> (no platform fee / creator earnings — F18) and
    /// total/offset/limit are added for the frontend infinite-scroll contract.
    /// URLs are resolved on per-request copies (see <see cref="WithResolvedUrls"/>)
    /// so the 15s-cached items are never mutated.
    /// </summary>
    private PublicCatalogPageResponse ToPublicEnvelope(PagedResult<TrackResponse> paged) => new()
    {
        Data = paged.Items.Select(t => PublicCatalogTrackDto.From(WithResolvedUrls(t))).ToList(),
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

    /// <summary>
    /// Per-request copy of a (possibly cached, shared) <see cref="TrackResponse"/>
    /// with audio/cover/creator-image URLs resolved for this request. The 15s
    /// <see cref="IMemoryCache"/> entries behind /discover and /catalog are shared
    /// across requests, so resolving in place would (a) bake one caller's Host
    /// header into responses served to everyone else for the cache window
    /// (host-header poisoning) and (b) mutate an object another request may be
    /// serializing concurrently. Cached items therefore stay unresolved and are
    /// never mutated; only these copies carry resolved URLs.
    /// </summary>
    private TrackResponse WithResolvedUrls(TrackResponse t) => new()
    {
        Id = t.Id,
        CambrianTrackId = t.CambrianTrackId,
        Title = t.Title,
        Description = t.Description,
        Genre = t.Genre,
        PrimaryGenre = t.PrimaryGenre,
        Subgenre = t.Subgenre,
        Mood = t.Mood,
        Tempo = t.Tempo,
        Tags = t.Tags,
        Instrumental = t.Instrumental,
        Visibility = t.Visibility,
        Price = t.Price,
        NonExclusivePrice = t.NonExclusivePrice,
        Status = t.Status,
        Duration = t.Duration,
        // Point audioUrl at the authenticated streaming proxy so browsers get the
        // correct Content-Type, Range support, and CORS headers (mirrors ResolveTrackUrls).
        AudioUrl = ResolveAbsoluteUrl($"/stream/{t.Id}/audio"),
        StreamAvailable = t.StreamAvailable,
        CoverArtUrl = string.IsNullOrEmpty(t.CoverArtUrl) ? t.CoverArtUrl : ResolveImageUrl(t.CoverArtUrl),
        CreatorId = t.CreatorId,
        CreatorSlug = t.CreatorSlug,
        CreatorProfileImageUrl = string.IsNullOrEmpty(t.CreatorProfileImageUrl) ? t.CreatorProfileImageUrl : ResolveImageUrl(t.CreatorProfileImageUrl),
        Artist = t.Artist,
        Plays = t.Plays,
        Sales = t.Sales,
        AuthorshipRecordId = t.AuthorshipRecordId,
        AiGenerated = t.AiGenerated,
        ProvenanceStatus = t.ProvenanceStatus,
        CreatedAt = t.CreatedAt,
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

    /// <summary>
    /// Public lyrics for a track. 404 when the track is not visible to the
    /// requester or has no lyrics. Rendered server-side by the frontend so
    /// lyrics are SEO-indexable.
    /// </summary>
    [HttpGet("tracks/{trackId}/lyrics")]
    public async Task<IActionResult> GetTrackLyrics(string trackId)
    {
        var track = await ResolveVisibleTrackAsync(trackId);
        if (track is null) return NotFoundResponse($"Track '{trackId}' not found.");

        var lyrics = await _trackDetails.GetLyricsAsync(Guid.Parse(track.Id));
        if (lyrics is null) return NotFoundResponse("This track has no lyrics.");
        return OkResponse(lyrics);
    }

    /// <summary>
    /// Public "Behind The Track" creation-process payload. 404 when the track
    /// is not visible to the requester or the creator hasn't shared a process.
    /// </summary>
    [HttpGet("tracks/{trackId}/behind-the-track")]
    public async Task<IActionResult> GetBehindTheTrack(string trackId)
    {
        var track = await ResolveVisibleTrackAsync(trackId);
        if (track is null) return NotFoundResponse($"Track '{trackId}' not found.");

        var process = await _trackDetails.GetCreationProcessAsync(Guid.Parse(track.Id));
        if (process is null) return NotFoundResponse("This track has no Behind The Track story.");
        return OkResponse(process);
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
        var ranked = await _catalog.GetTrendingPagedAsync(page, pageSize, genre, mood, tempo, instrumental, duration);
        ResolveTrackUrls(ranked.Items);
        return OkResponse(ranked.Items.Select(PublicCatalogTrackDto.From).ToList());
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
