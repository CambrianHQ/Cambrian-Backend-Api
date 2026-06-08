namespace Cambrian.Application.DTOs.CreatorProfile;

public class UpsertCreatorProfileRequest
{
    public string? Slug { get; set; }

    /// <summary>
    /// Public display name. When provided, it is written to the canonical Creator identity row
    /// (and mirrored to ApplicationUser) so it round-trips through GET /creator-profile/me and /auth/me.
    /// </summary>
    public string? DisplayName { get; set; }

    public string? Bio { get; set; }
    public string? Niche { get; set; }
    public List<SocialLinkDto>? SocialLinks { get; set; }
    public bool ShowEarnings { get; set; }
    public bool ShowDownloadStats { get; set; }
}
