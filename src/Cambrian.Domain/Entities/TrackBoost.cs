namespace Cambrian.Domain.Entities;

/// <summary>
/// Represents a single user "boosting" (upvoting) a track. Upvote-only — there
/// are no downvotes. One boost per user per track is enforced by a UNIQUE index
/// on (UserId, TrackId) in <c>CambrianDbContext</c>. Users cannot boost their
/// own tracks (enforced in the application layer).
/// </summary>
public sealed class TrackBoost
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>FK to AspNetUsers.Id — the user who boosted.</summary>
    public string UserId { get; set; } = "";

    /// <summary>FK to Tracks.Id — the boosted track.</summary>
    public Guid TrackId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ──
    public Track Track { get; set; } = null!;
}
