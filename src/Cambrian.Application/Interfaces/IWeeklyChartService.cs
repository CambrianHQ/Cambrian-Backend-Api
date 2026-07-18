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
}
