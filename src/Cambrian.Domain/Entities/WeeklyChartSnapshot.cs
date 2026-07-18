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

    /// <summary>
    /// Legacy persisted score column. New snapshots set this to the qualified
    /// weekly play count so existing readers retain the same field.
    /// </summary>
    public double Score { get; set; }

    /// <summary>
    /// Legacy 32-bit play-count column. New snapshots also populate
    /// <see cref="WeeklyQualifiedPlays"/> without truncation.
    /// </summary>
    public int PlaysInWindow { get; set; }

    /// <summary>Qualified play events inside [WeekStartUtc, WeekEndUtc).</summary>
    public long WeeklyQualifiedPlays { get; set; }

    /// <summary>Authoritative lifetime plays at the snapshot watermark.</summary>
    public long LifetimePlays { get; set; }

    /// <summary>
    /// What the score was computed from. "weekly_plays" is retained as the
    /// backward-compatible API value and now means qualified weekly plays.
    /// </summary>
    public string Basis { get; set; } = "weekly_plays";

    /// <summary>
    /// Inclusive qualified-event timestamp watermark used by this snapshot.
    /// Null only on snapshots written before watermark tracking was introduced.
    /// </summary>
    public DateTime? DataThroughUtc { get; set; }

    public DateTime ComputedAtUtc { get; set; } = DateTime.UtcNow;
}
