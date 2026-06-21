using System.ComponentModel.DataAnnotations;
using Cambrian.Application.Validation;

namespace Cambrian.Application.DTOs.CreatorProfile;

public class UpsertCreatorProfileRequest
{
    public string? Slug { get; set; }

    /// <summary>
    /// Public display name. When provided, it is written to the canonical Creator identity row
    /// (and mirrored to ApplicationUser) so it round-trips through GET /creator-profile/me and /auth/me.
    /// </summary>
    [StringLength(100)]
    [SafeMetadata]
    public string? DisplayName { get; set; }

    [StringLength(2000)]
    [SafeMetadata]
    public string? Bio { get; set; }
    [StringLength(100)]
    [SafeMetadata]
    public string? Niche { get; set; }
    public List<SocialLinkDto>? SocialLinks { get; set; }
    public bool ShowEarnings { get; set; }
    public bool ShowDownloadStats { get; set; }
}
