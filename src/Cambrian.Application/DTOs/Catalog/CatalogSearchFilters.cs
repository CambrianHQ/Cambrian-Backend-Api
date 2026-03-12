namespace Cambrian.Application.DTOs.Catalog;

/// <summary>
/// Optional search filters for catalog/discover/trending endpoints.
/// All fields are optional — omitting them returns unfiltered results.
/// </summary>
public class CatalogSearchFilters
{
    /// <summary>Filter by genre (e.g. "hip-hop", "electronic").</summary>
    public string? Genre { get; set; }

    /// <summary>Free-text search across title, artist, tags.</summary>
    public string? Search { get; set; }

    /// <summary>Sort order (e.g. "newest", "popular", "price-asc").</summary>
    public string? Sort { get; set; }

    /// <summary>Filter by mood (e.g. "happy", "dark", "chill", "energetic").</summary>
    public string? Mood { get; set; }

    /// <summary>
    /// Filter by tempo.
    /// Accepts named buckets ("slow", "medium", "fast") or a BPM range ("120-140").
    /// </summary>
    public string? Tempo { get; set; }

    /// <summary>
    /// Filter to instrumental-only tracks (true) or vocal tracks (false).
    /// Null = no filter.
    /// </summary>
    public bool? Instrumental { get; set; }

    /// <summary>
    /// Filter by duration bucket: "short" (&lt;2 min), "medium" (2-5 min), "long" (&gt;5 min).
    /// </summary>
    public string? Duration { get; set; }

    /// <summary>Page number (1-based, default 1).</summary>
    public int Page { get; set; } = 1;

    /// <summary>Page size (default 20).</summary>
    public int PageSize { get; set; } = 20;
}
