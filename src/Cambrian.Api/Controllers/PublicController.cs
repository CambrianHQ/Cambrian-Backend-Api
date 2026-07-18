using Cambrian.Application.DTOs.Public;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Public, read-only, unauthenticated discovery API consumed by the external MCP server
/// and by SEO / AI crawlers. Every endpoint:
/// <list type="bullet">
///   <item>requires no authentication and is safe to crawl;</item>
///   <item>returns only public data (no drafts/hidden content, storage keys, emails,
///         Stripe/payment data, or admin fields);</item>
///   <item>validates query parameters and enforces a hard page-size cap;</item>
///   <item>sets public cache headers and returns canonical URLs + SEO metadata.</item>
/// </list>
/// All work is delegated to <see cref="IPublicApiService"/> — this controller only handles HTTP.
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
[Produces("application/json")]
public sealed class PublicController : BaseController
{
    private const int ShortCacheSeconds = 15;      // count-bearing discovery data
    private const int LongCacheSeconds = 3600;     // 1 hour — evergreen content

    private readonly IPublicApiService _public;

    public PublicController(IPublicApiService publicApi) => _public = publicApi;

    /// <summary>Search and filter the public track catalogue.</summary>
    [HttpGet("tracks/search")]
    [ResponseCache(Duration = ShortCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PublicListResponse<PublicTrackDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SearchTracks(
        [FromQuery] string? q,
        [FromQuery] string? genre,
        [FromQuery] string? mood,
        [FromQuery] string? tempo,
        [FromQuery] bool? instrumental,
        [FromQuery] string? sort,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (!ValidatePaging(page, pageSize, out var error))
            return ErrorResponse(error);

        var result = await _public.SearchTracksAsync(q, genre, mood, tempo, instrumental, sort, page, pageSize);
        return OkResponse(result);
    }

    /// <summary>Get a single public track by its Cambrian track ID (CAMB-TRK-XXXX) or UUID.</summary>
    [HttpGet("tracks/{trackId}")]
    [ResponseCache(Duration = ShortCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PublicTrackDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTrack(string trackId)
    {
        var track = await _public.GetTrackAsync(trackId);
        return track is null ? NotFoundResponse("Track not found.") : OkResponse(track);
    }

    /// <summary>Search the public creator directory.</summary>
    [HttpGet("creators/search")]
    [ResponseCache(Duration = ShortCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PublicListResponse<PublicCreatorSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SearchCreators(
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (!ValidatePaging(page, pageSize, out var error))
            return ErrorResponse(error);

        var result = await _public.SearchCreatorsAsync(q, page, pageSize);
        return OkResponse(result);
    }

    /// <summary>Get a public creator profile by storefront slug.</summary>
    [HttpGet("creators/{slug}")]
    [ResponseCache(Duration = ShortCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PublicCreatorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCreator(string slug)
    {
        var creator = await _public.GetCreatorAsync(slug);
        return creator is null ? NotFoundResponse("Creator not found.") : OkResponse(creator);
    }

    /// <summary>List all genres in the public catalogue with real track counts.</summary>
    [HttpGet("genres")]
    [ResponseCache(Duration = ShortCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(IReadOnlyList<PublicGenreDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetGenres()
    {
        var genres = await _public.GetGenresAsync();
        return OkResponse(genres);
    }

    /// <summary>Get a single genre with a page of its public tracks.</summary>
    [HttpGet("genres/{genre}")]
    [ResponseCache(Duration = ShortCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PublicGenreDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetGenre(
        string genre,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (!ValidatePaging(page, pageSize, out var error))
            return ErrorResponse(error);

        var detail = await _public.GetGenreAsync(genre, page, pageSize);
        return detail is null ? NotFoundResponse("Genre not found.") : OkResponse(detail);
    }

    /// <summary>Trending tracks (ranked by real lifetime plays).</summary>
    [HttpGet("trending")]
    [ResponseCache(Duration = ShortCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PublicListResponse<PublicTrackDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetTrending(
        [FromQuery] string? genre,
        [FromQuery] string? mood,
        [FromQuery] string? tempo,
        [FromQuery] bool? instrumental,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (!ValidatePaging(page, pageSize, out var error))
            return ErrorResponse(error);

        var result = await _public.GetTrendingAsync(page, pageSize, genre, mood, tempo, instrumental);
        return OkResponse(result);
    }

    /// <summary>Latest releases (newest public tracks first).</summary>
    [HttpGet("latest")]
    [ResponseCache(Duration = ShortCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PublicListResponse<PublicTrackDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetLatest(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (!ValidatePaging(page, pageSize, out var error))
            return ErrorResponse(error);

        var result = await _public.GetLatestAsync(page, pageSize);
        return OkResponse(result);
    }

    /// <summary>Featured creators (ranked by number of public tracks).</summary>
    [HttpGet("featured-creators")]
    [ResponseCache(Duration = ShortCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(IReadOnlyList<PublicCreatorSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFeaturedCreators([FromQuery] int limit = 12)
    {
        var creators = await _public.GetFeaturedCreatorsAsync(limit);
        return OkResponse(creators);
    }

    /// <summary>Aggregate public platform statistics.</summary>
    [HttpGet("stats")]
    [ResponseCache(Duration = ShortCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PublicPlatformStatsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _public.GetPlatformStatsAsync();
        return OkResponse(stats);
    }

    /// <summary>Public pricing for creator plans (no Stripe price IDs).</summary>
    [HttpGet("pricing")]
    [ResponseCache(Duration = LongCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PublicPricingDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPricing()
    {
        var pricing = await _public.GetPricingAsync();
        return OkResponse(pricing);
    }

    /// <summary>Public FAQ for SEO / AI question answering.</summary>
    [HttpGet("faq")]
    [ResponseCache(Duration = LongCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PublicFaqDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFaq()
    {
        var faq = await _public.GetFaqAsync();
        return OkResponse(faq);
    }

    /// <summary>Sitemap entries (canonical URLs for public pages, tracks, and creators).</summary>
    [HttpGet("sitemap")]
    [ResponseCache(Duration = LongCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PublicSitemapDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSitemap()
    {
        var sitemap = await _public.GetSitemapAsync();
        return OkResponse(sitemap);
    }

    /// <summary>Public "Release Ready" information page.</summary>
    [HttpGet("release-ready")]
    [ResponseCache(Duration = LongCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PublicContentPageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReleaseReady()
    {
        var page = await _public.GetReleaseReadyAsync();
        return OkResponse(page);
    }

    /// <summary>Public "Authorship" information page.</summary>
    [HttpGet("authorship")]
    [ResponseCache(Duration = LongCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PublicContentPageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuthorship()
    {
        var page = await _public.GetAuthorshipAsync();
        return OkResponse(page);
    }

    /// <summary>Public "Creator Guide" information page.</summary>
    [HttpGet("creator-guide")]
    [ResponseCache(Duration = LongCacheSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PublicContentPageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCreatorGuide()
    {
        var page = await _public.GetCreatorGuideAsync();
        return OkResponse(page);
    }

    /// <summary>
    /// Validate pagination input. Page size beyond the cap is clamped downstream
    /// (<see cref="PublicApiService.MaxPageSize"/>); only non-positive values are rejected here.
    /// </summary>
    private static bool ValidatePaging(int page, int pageSize, out string error)
    {
        if (page < 1)
        {
            error = "page must be 1 or greater.";
            return false;
        }
        if (pageSize < 1)
        {
            error = "pageSize must be 1 or greater.";
            return false;
        }
        error = string.Empty;
        return true;
    }
}
