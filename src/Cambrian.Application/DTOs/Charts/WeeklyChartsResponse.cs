namespace Cambrian.Application.DTOs.Charts;

/// <summary>
/// Weekly "The Scene" chart — Top N tracks for the week plus a Track of the
/// Week. Mirrors the frontend contract in app/api/charts.ts.
/// </summary>
public sealed class WeeklyChartsResponse
{
    /// <summary>ISO-8601 start of the chart week (Monday 00:00 UTC).</summary>
    public string WeekOf { get; set; } = string.Empty;

    public IReadOnlyList<ChartEntryResponse> Entries { get; set; } = Array.Empty<ChartEntryResponse>();

    public TrackOfTheWeekResponse? TrackOfTheWeek { get; set; }
}

public sealed class ChartEntryResponse
{
    public int Rank { get; set; }
    public string TrackId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string CreatorId { get; set; } = string.Empty;
    public string? CoverArtUrl { get; set; }

    /// <summary>Change vs the previous aggregation (+ = moved up). Null when new.</summary>
    public int? DeltaRank { get; set; }
}

public sealed class TrackOfTheWeekResponse
{
    public string TrackId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string CreatorId { get; set; } = string.Empty;
    public string? CoverArtUrl { get; set; }
    public string? Description { get; set; }
}
