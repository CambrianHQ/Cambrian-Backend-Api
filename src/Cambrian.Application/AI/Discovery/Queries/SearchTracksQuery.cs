namespace Cambrian.Application.AI.Discovery.Queries;

/// <summary>
/// Structured search intent for AI-driven track discovery.
/// Supports natural-language context alongside structured filters.
/// </summary>
public class SearchTracksQuery
{
    /// <summary>Free-text query (e.g. "chill lofi beats for a cooking vlog").</summary>
    public string? Query { get; set; }

    /// <summary>Intended use case (e.g. "vlog", "podcast", "gaming", "ads").</summary>
    public string? UseCase { get; set; }

    // ── Structured filters (all optional) ──
    public string? Genre { get; set; }
    public string? Mood { get; set; }
    public string? Tempo { get; set; }
    public bool? Instrumental { get; set; }
    public string? Duration { get; set; }

    /// <summary>Maximum price in cents the buyer is willing to pay.</summary>
    public int? MaxPriceCents { get; set; }

    /// <summary>License type filter: "nonexclusive", "exclusive", "copyright_buyout".</summary>
    public string? LicenseType { get; set; }

    // ── Pagination ──
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
