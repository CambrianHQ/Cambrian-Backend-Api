namespace Cambrian.Domain.Entities;

/// <summary>
/// Denormalized, per-track engagement counters. One row per track (PK == TrackId).
///
/// <para>The transactional tables remain the source of truth (StreamSessions for plays,
/// TrackBoosts for likes, Purchases for sales, Tips for tips). These counters are
/// maintained incrementally as events occur so catalog/track-list reads avoid N+1
/// aggregation, and are periodically reconciled against the source tables by the stats
/// recompute job. A missing row is treated as all-zero.</para>
/// </summary>
public class TrackStat
{
    /// <summary>FK to Tracks.Id and the primary key (1:1 with Track).</summary>
    public Guid TrackId { get; set; }

    /// <summary>Lifetime count of valid plays (threshold-counted, de-duped). See StreamSession.</summary>
    public long PlayCount { get; set; }

    /// <summary>Lifetime count of likes. Sourced from TrackBoosts (the reused upvote signal).</summary>
    public int LikeCount { get; set; }

    /// <summary>Lifetime count of completed sales (Purchases with Status == "completed").</summary>
    public int SalesCount { get; set; }

    /// <summary>Lifetime count of one-time tips received on this track.</summary>
    public int TipCount { get; set; }

    /// <summary>Lifetime gross tip amount on this track, in cents.</summary>
    public long TipTotalCents { get; set; }

    /// <summary>UTC time of the most recent valid play; null if never played.</summary>
    public DateTime? LastPlayedAt { get; set; }

    /// <summary>UTC time this row was last written.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ──
    public Track Track { get; set; } = null!;
}
