namespace Cambrian.Domain.Entities;

/// <summary>
/// First-class creator identity. Primary key is a UUID.
/// Username is unique, normalized, and routable. Email is private (auth-only).
/// Tracks reference creators.id — never email or username.
/// </summary>
public sealed class Creator
{
    public Guid Id { get; set; }

    /// <summary>FK to AspNetUsers.Id (Identity string key).</summary>
    public string UserId { get; set; } = "";

    /// <summary>Unique, normalized, routable public username (lowercase, alphanumeric + hyphens).</summary>
    public string Username { get; set; } = "";

    public string? DisplayName { get; set; }

    public string Bio { get; set; } = "";

    public string? ProfileImageUrl { get; set; }

    public string? CoverImageUrl { get; set; }

    /// <summary>Stored as JSON array of { platform, url } objects.</summary>
    public string? SocialLinks { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ──
    public ApplicationUser User { get; set; } = null!;

    public ICollection<Track> Tracks { get; set; } = new List<Track>();
}
