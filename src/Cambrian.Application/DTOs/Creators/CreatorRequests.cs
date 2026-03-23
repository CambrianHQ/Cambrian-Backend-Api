using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Creators;

/// <summary>
/// Request body for PUT /api/creator/me — update own creator profile.
/// Email changes are explicitly forbidden through this endpoint.
/// </summary>
public class UpdateCreatorProfileRequest
{
    [StringLength(40, MinimumLength = 3)]
    [RegularExpression(@"^[a-z0-9][a-z0-9\-]*[a-z0-9]$",
        ErrorMessage = "Username must be lowercase alphanumeric with optional hyphens, no leading/trailing hyphens.")]
    public string? Username { get; set; }

    [StringLength(100)]
    public string? DisplayName { get; set; }

    [StringLength(2000)]
    public string? Bio { get; set; }

    public List<SocialLinkItemDto>? SocialLinks { get; set; }
}

/// <summary>
/// Response for GET /api/creators/username-availability?username=...
/// </summary>
public class UsernameAvailabilityResponse
{
    public string Username { get; set; } = "";
    public bool Available { get; set; }
}

/// <summary>
/// Response for POST /api/uploads/creator-image-url
/// </summary>
public class CreatorImageUploadResponse
{
    public string UploadUrl { get; set; } = "";
    public string PublicUrl { get; set; } = "";
}
