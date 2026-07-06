namespace Cambrian.Application.DTOs.Admin;

public class AdminTrack
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Genre { get; set; }

    public string CreatorId { get; set; } = string.Empty;

    public string? CreatorEmail { get; set; }

    public string Status { get; set; } = "available";

    public string Visibility { get; set; } = "public";

    public int NonExclusivePriceCents { get; set; }

    public int ExclusivePriceCents { get; set; }

    public int CopyrightBuyoutPriceCents { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool IsFeatured { get; set; }

    public DateTime? FeaturedAt { get; set; }

    public bool IsPinned { get; set; }

    public DateTime? PinnedAt { get; set; }
}
