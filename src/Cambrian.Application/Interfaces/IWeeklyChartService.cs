using Cambrian.Application.DTOs.Charts;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Computes and serves the persisted weekly "The Scene" chart. Public reads
/// lazily refresh missing, stale, or behind snapshots; the scheduled worker and
/// admin endpoint use the same idempotent aggregation boundary.
/// </summary>
public interface IWeeklyChartService
{
    /// <summary>Current-week chart, refreshed lazily when missing or stale.</summary>
    Task<WeeklyChartsResponse> GetCurrentAsync(CancellationToken ct = default);

    /// <summary>Recompute the chart now (admin trigger) and return the snapshot.</summary>
    Task<WeeklyChartsResponse> AggregateAsync(CancellationToken ct = default);

    /// <summary>
    /// COMPLETED chart weeks (the running week lives on /scene, never in the
    /// archive), newest first. Every persisted past week is included — the
    /// archive is the permanent public record of each week's final chart.
    /// </summary>
    Task<ChartArchiveIndexResponse> GetArchiveIndexAsync(int limit = 104, CancellationToken ct = default);

    /// <summary>
    /// Final chart of one COMPLETED week; null when the week has no snapshot
    /// or is the current/future week.
    /// </summary>
    Task<WeeklyChartsResponse?> GetArchivedWeekAsync(DateTime weekStartUtc, CancellationToken ct = default);
}
