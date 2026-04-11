namespace Cambrian.Application.DTOs.Catalog;

/// <summary>
/// Paginated catalog response shape returned by GET /catalog.
/// </summary>
public sealed class CatalogPageResponse
{
    public bool Success { get; init; } = true;

    public IReadOnlyCollection<TrackResponse> Data { get; init; } = Array.Empty<TrackResponse>();

    public int Page { get; init; }

    public int PageSize { get; init; }

    public int TotalCount { get; init; }

    public int TotalPages { get; init; }

    public bool HasNextPage { get; init; }

    public bool HasPreviousPage { get; init; }
}
