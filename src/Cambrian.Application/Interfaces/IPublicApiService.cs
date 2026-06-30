using Cambrian.Application.DTOs.Public;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Orchestrates the public, read-only, SEO/AI-safe discovery surface consumed by the
/// external MCP server. Every method returns public-safe DTOs (canonical URLs, public
/// images, real metrics only) and never exposes drafts/hidden content, storage keys,
/// emails, Stripe/payment data, or admin fields. Page sizes are clamped internally.
/// </summary>
public interface IPublicApiService
{
    Task<PublicListResponse<PublicTrackDto>> SearchTracksAsync(
        string? query, string? genre, string? mood, string? tempo, bool? instrumental,
        string? sort, int page, int pageSize);

    Task<PublicTrackDto?> GetTrackAsync(string trackId);

    Task<PublicListResponse<PublicTrackDto>> GetTrendingAsync(
        int page, int pageSize, string? genre, string? mood, string? tempo, bool? instrumental);

    Task<PublicListResponse<PublicTrackDto>> GetLatestAsync(int page, int pageSize);

    Task<PublicListResponse<PublicCreatorSummaryDto>> SearchCreatorsAsync(string? query, int page, int pageSize);

    Task<PublicCreatorDto?> GetCreatorAsync(string slug);

    Task<IReadOnlyList<PublicCreatorSummaryDto>> GetFeaturedCreatorsAsync(int limit);

    Task<IReadOnlyList<PublicGenreDto>> GetGenresAsync();

    Task<PublicGenreDetailDto?> GetGenreAsync(string genre, int page, int pageSize);

    Task<PublicPlatformStatsDto> GetPlatformStatsAsync();

    Task<PublicPricingDto> GetPricingAsync();

    Task<PublicFaqDto> GetFaqAsync();

    Task<PublicSitemapDto> GetSitemapAsync();

    Task<PublicContentPageDto> GetReleaseReadyAsync();

    Task<PublicContentPageDto> GetAuthorshipAsync();

    Task<PublicContentPageDto> GetCreatorGuideAsync();
}
