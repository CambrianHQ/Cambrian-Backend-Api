namespace Cambrian.Application.DTOs.Charts;

/// <summary>
/// Weekly "The Scene" chart — Top N tracks for the week plus a Track of the
/// Week. Mirrors the frontend contract in app/api/charts.ts.
/// </summary>
public sealed class WeeklyChartsResponse
{
    /// <summary>ISO-8601 start of the chart window (Monday 00:00 UTC, ISO week).</summary>
    public string WeekOf { get; set; } = string.Empty;

    /// <summary>ISO-8601 exclusive end of the chart window (WeekOf + 7 days).</summary>
    public string WeekEnd { get; set; } = string.Empty;

    public IReadOnlyList<ChartEntryResponse> Entries { get; set; } = Array.Empty<ChartEntryResponse>();

    public TrackOfTheWeekResponse? TrackOfTheWeek { get; set; }

    /// <summary>
    /// What the ranking was computed from: "weekly_plays" (stream sessions
    /// inside the chart week, on eligible public tracks) or "catalog_trending"
    /// (bootstrap fallback — newest eligible tracks — while the week has no
    /// qualified plays yet). Lets the frontend label the chart honestly.
    /// </summary>
    public string Basis { get; set; } = "weekly_plays";

    /// <summary>UTC timestamp this ranking was last (re)computed, null when never computed.</summary>
    public string? GeneratedAt { get; set; }

    /// <summary>
    /// UTC timestamp through which play data was included when this ranking was
    /// generated — <c>min(GeneratedAt, WeekEnd)</c>. For a completed/archived week
    /// this equals WeekEnd (the window had already closed); for the running week
    /// it equals GeneratedAt (the window is still open, so "through" is "as of
    /// last recompute"). Null when never computed.
    /// </summary>
    public string? DataThrough { get; set; }

    /// <summary>
    /// True when this response is not fresh: either the scheduled recompute has
    /// fallen behind its target cadence, or — for the running week specifically —
    /// no snapshot exists yet for it and an older week's chart is being served as
    /// a stand-in. Always false for a completed/archived week (a permanent record
    /// is never "stale"). The frontend should show a "may be outdated" indicator
    /// when this is true rather than silently presenting old data as current.
    /// </summary>
    public bool Stale { get; set; }
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

    /// <summary>
    /// Qualified plays: stream sessions started inside the chart window, on an
    /// eligible public track. This is the primary ranking input.
    /// </summary>
    public int PlaysInWindow { get; set; }

    /// <summary>All-time play count for this track, frozen as of GeneratedAt. Context only — never used for ranking.</summary>
    public long LifetimePlays { get; set; }

    /// <summary>The numeric ranking score. Today this equals PlaysInWindow; kept explicit so a future weighted score is auditable.</summary>
    public double Score { get; set; }
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
