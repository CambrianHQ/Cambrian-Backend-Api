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
}
