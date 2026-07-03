using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Contracts.Catalog;

/// <summary>
/// Query contract for GET /catalog.
/// </summary>
public sealed class CatalogQueryRequest
{
    [FromQuery(Name = "page")]
    public int Page { get; set; } = 1;

    [FromQuery(Name = "pageSize")]
    public int PageSize { get; set; } = 50;

    /// <summary>Frontend infinite-scroll contract: number of items to skip. When set (with
    /// <see cref="Limit"/>), takes precedence over page/pageSize. Interpreted in pages of
    /// <see cref="Limit"/> (offset = pageIndex × limit), which is how infinite scroll pages.</summary>
    [FromQuery(Name = "offset")]
    public int? Offset { get; set; }

    /// <summary>Frontend infinite-scroll contract: page size (capped at 60).</summary>
    [FromQuery(Name = "limit")]
    public int? Limit { get; set; }

    [FromQuery(Name = "genre")]
    public string? Genre { get; set; }

    [FromQuery(Name = "search")]
    public string? Search { get; set; }

    [FromQuery(Name = "sort")]
    public string? Sort { get; set; }

    [FromQuery(Name = "mood")]
    public string? Mood { get; set; }

    [FromQuery(Name = "tempo")]
    public string? Tempo { get; set; }

    [FromQuery(Name = "instrumental")]
    public bool? Instrumental { get; set; }

    [FromQuery(Name = "duration")]
    public string? Duration { get; set; }
}
