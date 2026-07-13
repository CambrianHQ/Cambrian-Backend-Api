using Cambrian.Application.DTOs.Charts;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Computes and serves the weekly "The Scene" chart. Recompute is scheduled
/// (WeeklyChartWorker, every WeeklyChartService.RecomputeInterval — see that
/// constant for the current freshness target) AND admin-triggerable on demand
/// (POST /admin/charts/aggregate) — both call the same idempotent AggregateAsync,
/// so overlapping triggers are harmless. Reads are served from the persisted
/// WeeklyChartSnapshots table, not an in-process cache.
/// </summary>
public interface IWeeklyChartService
{
    /// <summary>Current persisted chart for the running week; computes it once if nothing has ever been persisted.</summary>
    Task<WeeklyChartsResponse> GetCurrentAsync(CancellationToken ct = default);

    /// <summary>Recompute the chart now (scheduled worker or admin trigger) and return the snapshot.</summary>
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
