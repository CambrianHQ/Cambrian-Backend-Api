using Cambrian.Application.DTOs.Charts;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Computes and serves the weekly "The Scene" chart. Aggregation is on-demand
/// (admin-triggered) and cached in-process — there is no scheduled job yet, so
/// the admin trigger is how charts become populated/testable. (residue R17)
/// </summary>
public interface IWeeklyChartService
{
    /// <summary>Current cached chart; aggregates lazily on first access.</summary>
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
