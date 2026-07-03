using Cambrian.Application.Interfaces;

namespace Cambrian.Api.BackgroundServices;

/// <summary>
/// Scheduled recompute for the weekly Scene chart. Runs once at startup
/// (catch-up after deploys) and then every <see cref="Interval"/>; recompute
/// is idempotent per week (WeeklyChartService replaces the week's rows in one
/// transaction), so overlapping admin triggers are harmless. Mirrors the
/// MasteringWorker pattern: PeriodicTimer + never let one tick kill the loop.
/// </summary>
public sealed class WeeklyChartWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    private readonly IWeeklyChartService _charts;
    private readonly ILogger<WeeklyChartWorker> _logger;

    public WeeklyChartWorker(IWeeklyChartService charts, ILogger<WeeklyChartWorker> logger)
    {
        _charts = charts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EVENT: WeeklyChartWorkerStarted intervalHours:{Hours}", Interval.TotalHours);

        // Small startup delay so we don't compete with app warmup / migrations.
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RecomputeAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (await WaitAsync(timer, stoppingToken))
        {
            await RecomputeAsync(stoppingToken);
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RecomputeAsync(CancellationToken ct)
    {
        try
        {
            var chart = await _charts.AggregateAsync(ct);
            _logger.LogInformation(
                "EVENT: WeeklyChartRecomputed weekOf:{WeekOf} entries:{Entries} basis:{Basis}",
                chart.WeekOf, chart.Entries.Count, chart.Basis);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EVENT: WeeklyChartRecomputeFailed");
        }
    }
}
