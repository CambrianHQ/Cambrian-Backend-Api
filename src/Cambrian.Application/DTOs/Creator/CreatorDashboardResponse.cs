namespace Cambrian.Application.DTOs.Creator;

public class CreatorDashboardResponse
{
    public long EarningsCents { get; set; }
    public long WeeklyEarningsCents { get; set; }
    public int TotalPlays { get; set; }
    public int TotalSales { get; set; }
    public decimal ConversionRate { get; set; }
    public List<TrackStatsDto> Tracks { get; set; } = new();
}

public class TrackStatsDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? CoverArtUrl { get; set; }
    public int Sales { get; set; }
    public int Plays { get; set; }
    public long EarnedCents { get; set; }
}

public class CreatorDashboardTrackSummary
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string? CoverArtUrl { get; set; }
}

public class CreatorTrackSummary
{
    public Guid Id { get; set; }
    public string CambrianTrackId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Genre { get; set; }
    public string? Mood { get; set; }
    public string? Tempo { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool Instrumental { get; set; }
    public string Visibility { get; set; } = "public";
    public decimal Price { get; set; }
    public int NonExclusivePriceCents { get; set; }
    public int ExclusivePriceCents { get; set; }
    public int CopyrightBuyoutPriceCents { get; set; }
    public bool ExclusiveSold { get; set; }
    public string Status { get; set; } = "available";
    public string? LicenseType { get; set; }
    public string? Duration { get; set; }
    public string? AudioUrl { get; set; }
    public string? CoverArtUrl { get; set; }
    public DateTime CreatedAt { get; set; }
}
