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

    // --- "What's in my studio" (DAW, AI tools, instruments, plugins, gear, workflow) ---
    public string? StudioSetup { get; set; } // stored as JSON string (SocialLinks precedent)

    // --- Artist journey timeline (updates, milestones, photos, shows) ---
    public string? JourneyEntries { get; set; } // stored as JSON array string

    // --- Public stats toggle ---
    public bool ShowEarnings { get; set; }
    public bool ShowDownloadStats { get; set; }

    /// <summary>Comma-separated track GUIDs pinned by the creator for storefront display order.</summary>
    public string? PinnedTrackIds { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
