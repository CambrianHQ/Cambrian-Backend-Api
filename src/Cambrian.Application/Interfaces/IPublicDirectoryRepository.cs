namespace Cambrian.Application.Interfaces;

/// <summary>
/// Read-only data source for the public discovery surface that is NOT already served by
/// <see cref="ICatalogService"/>: platform aggregates, genre counts, creator search,
/// featured creators, and sitemap references. Every query is scoped to public,
/// non-removed, non-hidden content. Returns plain value records — no domain entities,
/// no storage keys, no private fields.
/// </summary>
public interface IPublicDirectoryRepository
{
    /// <summary>Aggregate public counts for the platform-stats endpoint.</summary>
    Task<PublicPlatformCounts> GetPlatformCountsAsync();

    /// <summary>Distinct genres present in the public catalogue with real track counts.</summary>
    Task<IReadOnlyList<PublicGenreCount>> GetGenreCountsAsync();

    /// <summary>Paged creator directory search by name/slug/bio/niche.</summary>
    Task<PublicCreatorSearchResult> SearchCreatorsAsync(string? query, int page, int pageSize);

    /// <summary>Creators ranked by number of public tracks (a real "featured" proxy).</summary>
    Task<IReadOnlyList<PublicCreatorRow>> GetFeaturedCreatorsAsync(int limit);

    /// <summary>Canonical-URL source data for the sitemap endpoint, capped to safe maxima.</summary>
    Task<PublicSitemapData> GetSitemapDataAsync(int maxTracks, int maxCreators);
}

/// <summary>Aggregate public platform counts.</summary>
public sealed record PublicPlatformCounts(int TrackCount, int CreatorCount, int GenreCount, int TotalPlays);

/// <summary>A genre and its public track count.</summary>
public sealed record PublicGenreCount(string Name, int TrackCount);

/// <summary>A public creator directory row (no email, no Stripe, no wallet).</summary>
public sealed record PublicCreatorRow(
    string Id,
    string Slug,
    string? Username,
    string DisplayName,
    string? Niche,
    string? Bio,
    string? ImageUrl,
    DateTime UpdatedAt,
    int TrackCount);

/// <summary>A page of creator directory rows plus the total match count.</summary>
public sealed record PublicCreatorSearchResult(IReadOnlyList<PublicCreatorRow> Rows, int TotalCount);

/// <summary>A public track reference for the sitemap.</summary>
public sealed record PublicSitemapTrackRef(string CambrianTrackId, DateTime LastModified);

/// <summary>A public creator reference for the sitemap.</summary>
public sealed record PublicSitemapCreatorRef(string Slug, DateTime LastModified);

/// <summary>Sitemap source data: capped lists of public tracks and creators.</summary>
public sealed record PublicSitemapData(
    IReadOnlyList<PublicSitemapTrackRef> Tracks,
    IReadOnlyList<PublicSitemapCreatorRef> Creators);
