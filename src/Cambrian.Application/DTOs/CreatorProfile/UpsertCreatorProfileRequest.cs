namespace Cambrian.Application.DTOs.CreatorProfile;

public class UpsertCreatorProfileRequest
{
    public string? Slug { get; set; }
    public string? Bio { get; set; }
    public string? Niche { get; set; }
    public List<SocialLinkDto>? SocialLinks { get; set; }
    public bool ShowEarnings { get; set; }
    public bool ShowDownloadStats { get; set; }
}
