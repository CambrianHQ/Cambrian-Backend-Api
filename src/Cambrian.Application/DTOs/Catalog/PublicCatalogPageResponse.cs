namespace Cambrian.Application.DTOs.Catalog;

/// <summary>
/// Anonymous-safe paginated catalog envelope returned by GET /catalog and
/// GET /discover. Mirrors the legacy <see cref="CatalogPageResponse"/> field set
/// (so existing clients keep working) but serializes <see cref="PublicTrackDto"/>
/// items — never <see cref="TrackResponse"/> — and adds the <c>total</c>/<c>offset</c>/
/// <c>limit</c> fields the frontend infinite-scroll contract reads.
/// </summary>
public sealed class PublicCatalogPageResponse
{
    public bool Success { get; init; } = true;

    public IReadOnlyCollection<PublicCatalogTrackDto> Data { get; init; } = Array.Empty<PublicCatalogTrackDto>();

    public int Page { get; init; }

    public int PageSize { get; init; }

    /// <summary>Zero-based index of the first item on this page (frontend infinite-scroll contract).</summary>
    public int Offset { get; init; }

    /// <summary>Page size, echoed under the frontend's preferred name.</summary>
    public int Limit { get; init; }

    /// <summary>Total number of tracks matching the query (frontend infinite-scroll contract).</summary>
    public int Total { get; init; }

    /// <summary>Legacy alias for <see cref="Total"/>; retained for backward compatibility.</summary>
    public int TotalCount { get; init; }

    public int TotalPages { get; init; }

    public bool HasNextPage { get; init; }

    public bool HasPreviousPage { get; init; }
}
