namespace Cambrian.Domain.Entities;

public class StreamSession
{
    public Guid Id { get; set; }

    public Guid TrackId { get; set; }

    public Track Track { get; set; } = null!;

    public string? UserId { get; set; }

    public string? Title { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC start of the currently active playback segment.</summary>
    public DateTime? LastStartedAtUtc { get; set; }

    public DateTime? StoppedAt { get; set; }

    /// <summary>
    /// Privacy-safe stable listener key used for ownership, replay deduplication,
    /// and concurrency control. Raw IP addresses are never persisted.
    /// Null only for sessions created before qualified-play tracking shipped.
    /// </summary>
    public string? ListenerKeyHash { get; set; }

    /// <summary>Hash of the caller-provided anonymous session identifier, when anonymous.</summary>
    public string? AnonymousSessionHash { get; set; }

    /// <summary>Client request idempotency key for starting this playback session.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Whether the creator was previewing their own track.</summary>
    public bool IsOwnerPreview { get; set; }

    /// <summary>Whether the track was public, published, and playable when the session began.</summary>
    public bool WasEligibleAtStart { get; set; }

    /// <summary>Server-calculated seconds required before this session can qualify.</summary>
    public double QualificationThresholdSeconds { get; set; }

    /// <summary>Accumulated active playback seconds across pause/resume segments.</summary>
    public double ActivePlaybackSeconds { get; set; }

    /// <summary>pending, qualified, deduplicated, owner_preview, ineligible, bot, or legacy_unqualified.</summary>
    public string QualificationStatus { get; set; } = "pending";

    public DateTime? QualifiedAtUtc { get; set; }
}
