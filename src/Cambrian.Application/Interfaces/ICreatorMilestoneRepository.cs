namespace Cambrian.Application.Interfaces;

/// <summary>First play a creator ever received (anonymous sessions count).</summary>
public sealed class FirstPlayMilestone
{
    public DateTime AtUtc { get; set; }
    public Guid TrackId { get; set; }
}

/// <summary>
/// First fan signal a creator ever received. Source is one of
/// "follow" | "save" | "support" | "subscription" — never the fan's identity.
/// </summary>
public sealed class FirstFanMilestone
{
    public DateTime AtUtc { get; set; }
    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// Read-only lookups behind the creator lifecycle milestones
/// (first_play_received / first_fan_event). Computed from existing tables —
/// StreamSessions, CreatorFollows, TrackBoosts, Purchases, FanSubscriptions —
/// so the milestones are idempotent by construction: the earliest timestamp
/// never changes once set, and no new write path exists.
/// </summary>
public interface ICreatorMilestoneRepository
{
    /// <summary>Earliest stream session on any of the creator's tracks (null when unplayed).</summary>
    Task<FirstPlayMilestone?> GetFirstPlayAsync(string userId, CancellationToken ct = default);

    /// <summary>Earliest fan signal (follow/save/support/subscription) for the creator (null when none).</summary>
    Task<FirstFanMilestone?> GetFirstFanEventAsync(string userId, CancellationToken ct = default);
}
