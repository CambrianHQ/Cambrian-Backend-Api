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

    /// <summary>
    /// What the ranking was computed from: "weekly_plays" (stream sessions
    /// inside the chart week) or "catalog_trending" (bootstrap fallback while
    /// the week has no plays). Lets the frontend label the chart honestly.
    /// </summary>
    public string Basis { get; set; } = "weekly_plays";

    /// <summary>UTC timestamp of the last recompute, null when never computed.</summary>
    public string? ComputedAt { get; set; }
}

public sealed class ChartEntryResponse
{
    public int Rank { get; set; }
    public string TrackId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string CreatorId { get; set; } = string.Empty;

    /// <summary>
    /// Routable creator username (resolved at read time so links survive
    /// renames), null when the creator identity row is gone. Frontends link
    /// artists as /@{creatorUsername}.
    /// </summary>
    public string? CreatorUsername { get; set; }

    public string? CoverArtUrl { get; set; }

    /// <summary>Change vs the previous aggregation (+ = moved up). Null when new.</summary>
    public int? DeltaRank { get; set; }

    /// <summary>Stream sessions started inside the chart week (archive surface).</summary>
    public int PlaysInWindow { get; set; }
}

/// <summary>GET /api/charts/weekly/archive — completed chart weeks, newest first.</summary>
public sealed class ChartArchiveIndexResponse
{
    public IReadOnlyList<ChartArchiveWeekSummary> Weeks { get; set; } = Array.Empty<ChartArchiveWeekSummary>();
}

public sealed class ChartArchiveWeekSummary
{
    /// <summary>ISO week key used in URLs, e.g. "2026-w28".</summary>
    public string IsoWeek { get; set; } = string.Empty;

    /// <summary>ISO-8601 start of the chart week (Monday 00:00 UTC).</summary>
    public string WeekOf { get; set; } = string.Empty;

    /// <summary>ISO-8601 exclusive end of the chart week.</summary>
    public string WeekEnd { get; set; } = string.Empty;

    public int Entries { get; set; }

    public string? TopTrackId { get; set; }
    public string? TopTrackTitle { get; set; }
    public string? TopTrackArtist { get; set; }
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
