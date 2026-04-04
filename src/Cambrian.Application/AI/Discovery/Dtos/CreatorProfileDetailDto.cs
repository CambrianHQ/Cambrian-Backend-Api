namespace Cambrian.Application.AI.Discovery.Dtos;

public class CreatorProfileDetailDto
{
    public string UserId { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? Niche { get; set; }
    public string? ProfileImageUrl { get; set; }
    public string? CoverImageUrl { get; set; }
    public string? Slug { get; set; }
    public int TrackCount { get; set; }
    public int FollowerCount { get; set; }
}
