using Cambrian.Application.Interfaces;

namespace Cambrian.Api.BackgroundServices;

/// <summary>
/// Scheduled recompute for the weekly Scene chart. Runs once at startup
/// (catch-up after deploys) and then at the configured interval; recompute
/// is idempotent per week (WeeklyChartService replaces the week's rows in one
/// transaction), so overlapping admin triggers are harmless. Mirrors the
/// MasteringWorker pattern: PeriodicTimer + never let one tick kill the loop.
/// </summary>
public sealed class WeeklyChartWorker : BackgroundService
{
    private const int DefaultIntervalSeconds = 30;
    private const int DefaultStartupDelaySeconds = 15;

    private readonly IWeeklyChartService _charts;
    private readonly ILogger<WeeklyChartWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _startupDelay;

    public WeeklyChartWorker(
        IWeeklyChartService charts,
        IConfiguration configuration,
        ILogger<WeeklyChartWorker> logger)
    {
        _charts = charts;
        _logger = logger;

        var intervalSeconds = configuration.GetValue<int?>("Charts:Weekly:WorkerIntervalSeconds")
            ?? DefaultIntervalSeconds;
        var startupDelaySeconds = configuration.GetValue<int?>("Charts:Weekly:StartupDelaySeconds")
            ?? DefaultStartupDelaySeconds;
        _interval = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));
        _startupDelay = TimeSpan.FromSeconds(Math.Max(0, startupDelaySeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "EVENT: WeeklyChartWorkerStarted intervalSeconds:{Seconds}",
            _interval.TotalSeconds);

        // Small startup delay so we don't compete with app warmup / migrations.
        try
        {
            await Task.Delay(_startupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RecomputeAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
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
