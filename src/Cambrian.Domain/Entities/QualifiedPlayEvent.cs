namespace Cambrian.Domain.Entities;

/// <summary>
/// Append-only evidence that a playback crossed the server-defined qualification
/// threshold. This ledger is the source of truth for every post-migration play.
/// </summary>
public class QualifiedPlayEvent
{
    public Guid Id { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public Guid TrackId { get; set; }
    public Track Track { get; set; } = null!;
    public string CreatorId { get; set; } = null!;
    public string? ListenerUserId { get; set; }
    public string ListenerKeyHash { get; set; } = null!;
    public string? AnonymousSessionHash { get; set; }
    public Guid PlaybackSessionId { get; set; }
    public StreamSession PlaybackSession { get; set; } = null!;
    public DateTime QualifiedAtUtc { get; set; }
    public string QualificationBasis { get; set; } = "active_playback_threshold";
    public double ActivePlaybackSeconds { get; set; }
    public double ThresholdSeconds { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Set in the same transaction as the TrackStats increment.</summary>
    public DateTime? AggregatedAtUtc { get; set; }
}
