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
