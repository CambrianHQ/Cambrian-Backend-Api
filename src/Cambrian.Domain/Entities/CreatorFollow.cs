namespace Cambrian.Domain.Entities;

/// <summary>
/// Represents a user following a creator.
/// </summary>
public sealed class CreatorFollow
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>FK to AspNetUsers.Id — the user who is following.</summary>
    public string FollowerId { get; set; } = "";

    /// <summary>FK to Creators.Id — the creator being followed.</summary>
    public Guid CreatorId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ──
    public Creator Creator { get; set; } = null!;
}
