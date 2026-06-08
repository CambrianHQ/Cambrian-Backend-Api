namespace Cambrian.Application.DTOs.Community;

/// <summary>
/// A track on the "Hot This Week" chart: its rank, its boost count within the
/// rolling 7-day window, and enough track/creator fields to render a row.
/// </summary>
public sealed class HotTrackResponse
{
    public int Rank { get; set; }

    /// <summary>Boosts received within the 7-day window (not all-time).</summary>
    public int BoostCount { get; set; }

    /// <summary>Whether the current user has boosted this track.</summary>
    public bool HasBoosted { get; set; }

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Genre { get; set; }
    public string? CoverArtUrl { get; set; }
    public string? AudioUrl { get; set; }
    public string CreatorId { get; set; } = "";
    public string? CreatorName { get; set; }
    public string? CreatorSlug { get; set; }
}
