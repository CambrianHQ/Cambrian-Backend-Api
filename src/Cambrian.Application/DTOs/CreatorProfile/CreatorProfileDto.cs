namespace Cambrian.Application.DTOs.CreatorProfile;

public class CreatorProfileDto
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Slug { get; set; } = "";

    public string Bio { get; set; } = "";
    public string? Niche { get; set; }

    public string? ProfileImageUrl { get; set; }
    public string? BannerImageUrl { get; set; }

    public List<SocialLinkDto>? SocialLinks { get; set; }

    public CreatorStatsDto Stats { get; set; } = new();

    public bool ShowEarnings { get; set; }
    public bool ShowDownloadStats { get; set; }

    /// <summary>Comma-separated track GUIDs pinned by the creator.</summary>
    public string? PinnedTrackIds { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
