using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.Interfaces;

public interface ICatalogService
{
    Task<IReadOnlyCollection<TrackResponse>> GetCatalogAsync(int page = 1, int pageSize = 50, string? genre = null, string? search = null, string? sort = null);

    Task<IReadOnlyCollection<TrackResponse>> GetCatalogAsync(int page, int pageSize, string? genre, string? search, string? sort,
        string? mood, string? tempo, bool? instrumental, string? duration);

    Task<PagedResult<TrackResponse>> GetCatalogPagedAsync(int page, int pageSize, string? genre, string? search, string? sort,
        string? mood, string? tempo, bool? instrumental, string? duration);

    Task<IReadOnlyCollection<TrackResponse>> GetDiscoverAsync(int page = 1, int pageSize = 20, string? genre = null, string? search = null);

    Task<IReadOnlyCollection<TrackResponse>> GetDiscoverAsync(int page, int pageSize, string? genre, string? search,
        string? mood, string? tempo, bool? instrumental, string? duration);

    Task<PagedResult<TrackResponse>> GetDiscoverPagedAsync(int page, int pageSize, string? genre, string? search,
        string? mood, string? tempo, bool? instrumental, string? duration);

    Task<TrackResponse?> GetTrackAsync(string trackId);

    /// <summary>
    /// Trending page ranked by a REAL signal: recent public tracks ordered by lifetime
    /// play count (descending), then newest. The legacy <c>sort=trending</c> ordering is a
    /// no-op over the unpopulated <c>TrendingScore</c> column and is intentionally not used.
    /// </summary>
    Task<PagedResult<TrackResponse>> GetTrendingPagedAsync(int page, int pageSize,
        string? genre, string? mood, string? tempo, bool? instrumental, string? duration);

    /// <summary>Public, storefront-safe tracks for a single creator, paged (newest first).</summary>
    Task<PagedResult<TrackResponse>> GetByCreatorPagedAsync(string creatorId, Guid? creatorUuid, int page, int pageSize);
}