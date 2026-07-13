namespace Cambrian.Domain.Entities;

/// <summary>
/// Denormalized, per-creator aggregate metrics. One row per creator (PK == CreatorId,
/// the Creators.Id UUID).
///
/// <para>Source-of-truth tables: CreatorFollows (followers), Tracks (track count / latest
/// release / plays via StreamSessions), FanSubscriptions (subscribers), EarningsTransactions
/// + Tips (tips received). These values are maintained incrementally where cheap and fully
/// reconciled by the stats recompute job, which also owns the time-windowed fields
/// (<see cref="MonthlyPlays"/>) and the <see cref="TrendingScore"/>. A missing row is
/// treated as all-zero.</para>
/// </summary>
public class CreatorStat
{
    /// <summary>FK to Creators.Id and the primary key (1:1 with Creator).</summary>
    public Guid CreatorId { get; set; }

    /// <summary>Current follower count. Source: CreatorFollows.</summary>
    public int FollowerCount { get; set; }

    /// <summary>Number of published tracks owned by this creator.</summary>
    public int TrackCount { get; set; }

    /// <summary>Lifetime plays across all of the creator's tracks.</summary>
    public long TotalPlays { get; set; }

    /// <summary>
    /// Lifetime count of distinct listeners across all of the creator's tracks. Maintained by the
    /// reconciliation/rebuild pass, not incrementally on every play.
    /// </summary>
    public long UniqueListenerCount { get; set; }

    /// <summary>Plays in the trailing 30 days. Recomputed by the stats job (decays over time).</summary>
    public long MonthlyPlays { get; set; }

    /// <summary>Active fan subscribers (FanSubscriptions with Status == "active").</summary>
    public int SubscriberCount { get; set; }

    /// <summary>Lifetime count of money-in support events (one-time tips + fan-sub payments).</summary>
    public int TipCount { get; set; }

    /// <summary>Lifetime gross tips + fan-subscription revenue received, in cents.</summary>
    public long TipsReceivedCents { get; set; }

    /// <summary>UTC time of the creator's most recent track release; null if none.</summary>
    public DateTime? LatestReleaseAt { get; set; }

    /// <summary>Decayed engagement score used for ranking creators. Owned by the stats job.</summary>
    public decimal TrendingScore { get; set; }

    /// <summary>UTC time this row was last written.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ──
    public Creator Creator { get; set; } = null!;
}
