namespace Cambrian.Application.DTOs.Creators;

/// <summary>Request body for POST /creators/search.</summary>
public class CreatorSearchRequest
{
    public string? Query { get; set; }

    /// <summary>Max results to return (clamped server-side to 1..50).</summary>
    public int? Limit { get; set; }
}

/// <summary>
/// Lightweight creator match returned by the search endpoint. Excludes the full track list and
/// email, so the Search page can render creator cards without an extra round trip.
/// </summary>
public class CreatorSearchResultDto
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string? DisplayName { get; set; }

    /// <summary>
    /// Public profile slug when the creator customized one; falls back to the
    /// username. Frontends build /@{slug} links from this — it is never empty.
    /// </summary>
    public string Slug { get; set; } = "";

    public string? ProfileImageUrl { get; set; }
    public string Bio { get; set; } = "";
    public int TrackCount { get; set; }
}
