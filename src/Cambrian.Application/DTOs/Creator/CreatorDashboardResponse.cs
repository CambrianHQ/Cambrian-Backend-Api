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
