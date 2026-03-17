namespace Cambrian.Domain.Entities;

public sealed class CreatorProfile
{
    public Guid Id { get; set; }

    /// <summary>FK to AspNetUsers.Id (string in Identity).</summary>
    public string UserId { get; set; } = "";

    public string Slug { get; set; } = "";

    // --- Branding ---
    public string? BannerImageUrl { get; set; }
    public string? ProfileImageUrl { get; set; }

    // --- Bio + niche ---
    public string Bio { get; set; } = "";
    public string? Niche { get; set; } // e.g. "cinematic AI composer"

    // --- Social links (JSON or pipe-delimited) ---
    public string? SocialLinks { get; set; } // stored as JSON string

    // --- Public stats toggle ---
    public bool ShowEarnings { get; set; }
    public bool ShowDownloadStats { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
