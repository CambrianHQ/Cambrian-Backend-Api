namespace Cambrian.Application.AI.Discovery.Dtos;

/// <summary>
/// Full track detail response for AI consumers. Extends search result with
/// complete license options and creator profile context.
/// </summary>
public class TrackDetailsDto
{
    public string TrackId { get; set; } = string.Empty;
    public string CambrianTrackId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    public CreatorSummaryDto Creator { get; set; } = new();
    public TrackAttributesDto Attributes { get; set; } = new();
    public TrackPreviewDto Preview { get; set; } = new();
    public LicenseSummaryDto License { get; set; } = new();

    public string Status { get; set; } = "available";
    public string Visibility { get; set; } = "public";
    public bool ExclusiveSold { get; set; }
    public bool IsCopyrightTransferred { get; set; }

    public decimal TrendingScore { get; set; }
    public DateTime CreatedAt { get; set; }
}
