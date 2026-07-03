namespace Cambrian.Domain.Entities;

/// <summary>
/// One persisted rank row of a weekly Scene chart. A week's chart is the set of
/// rows sharing a WeekStartUtc; recompute for a week is idempotent (the week's
/// rows are replaced in one transaction). Title/artist/cover are denormalized
/// so historical charts show what was true that week, and rank deltas come from
/// the previous week's persisted rows — never from process memory.
/// </summary>
public class WeeklyChartSnapshot
{
    public Guid Id { get; set; }

    /// <summary>UTC Monday 00:00 that starts the chart week (ISO week).</summary>
    public DateTime WeekStartUtc { get; set; }

    /// <summary>Exclusive end of the chart week (WeekStartUtc + 7 days).</summary>
    public DateTime WeekEndUtc { get; set; }

    /// <summary>1-based position within the week's chart.</summary>
    public int Rank { get; set; }

    /// <summary>Rank in the previous week's chart, null when the track is new to the chart.</summary>
    public int? PreviousRank { get; set; }

    /// <summary>PreviousRank - Rank (positive = moved up). Null when new.</summary>
    public int? DeltaRank { get; set; }

    public Guid TrackId { get; set; }

    public string CreatorId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;

    public string? CoverArtUrl { get; set; }

    /// <summary>Composite ranking score (primary input: plays in window).</summary>
    public double Score { get; set; }

    /// <summary>Stream sessions started within the chart window.</summary>
    public int PlaysInWindow { get; set; }

    /// <summary>
    /// What the score was computed from: "weekly_plays" once real in-window
    /// plays exist, or "catalog_trending" during the bootstrap period.
    /// The frontend uses this to label the chart honestly.
    /// </summary>
    public string Basis { get; set; } = "weekly_plays";

    public DateTime ComputedAtUtc { get; set; } = DateTime.UtcNow;
}
