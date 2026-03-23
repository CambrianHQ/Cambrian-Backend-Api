namespace Cambrian.Application.DTOs.Creators;

using Cambrian.Application.DTOs.Catalog;

/// <summary>
/// Public creator response shape. Never includes email.
/// </summary>
public class PublicCreatorDto
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string? DisplayName { get; set; }
    public string Bio { get; set; } = "";
    public string? ProfileImageUrl { get; set; }
    public string? CoverImageUrl { get; set; }
    public List<SocialLinkItemDto>? SocialLinks { get; set; }
    public CreatorStatsResponseDto Stats { get; set; } = new();
    public List<TrackResponse> Tracks { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SocialLinkItemDto
{
    public string Platform { get; set; } = "";
    public string Url { get; set; } = "";
}

public class CreatorStatsResponseDto
{
    public int TrackCount { get; set; }
    public int TotalSales { get; set; }
    public int TotalDownloads { get; set; }
    public decimal AverageRating { get; set; }
    public int FollowerCount { get; set; }
}
