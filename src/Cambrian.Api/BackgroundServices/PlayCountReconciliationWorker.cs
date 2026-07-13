using Cambrian.Application.DTOs.PlayCounts;
using Cambrian.Application.Interfaces;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.BackgroundServices;

/// <summary>
/// Periodically compares the TrackStats/CreatorStats projection against durable, qualified
/// StreamSessions and repairs drift. Runs once shortly after startup (so a fresh deploy's empty
/// projection gets backfilled from whatever historical StreamSessions already exist — a real
/// recompute from real events, not fabricated data) and then on <c>PlayCounts:ReconciliationIntervalHours</c>
/// (default 6h). Ongoing ticks default to dry-run (detect + record mismatches, don't write)
/// unless <c>PlayCounts:AutoRepair</c> is enabled — the initial cold-start backfill is the one
/// exception, since a still-empty projection would otherwise just re-log "N mismatches" forever
/// without ever fixing them. Mirrors SubscriptionExpiryWorker/WeeklyChartWorker: PeriodicTimer,
/// scoped per tick, never lets one tick's failure kill the loop.
/// </summary>
public sealed class PlayCountReconciliationWorker : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlayCountReconciliationWorker> _logger;

    public PlayCountReconciliationWorker(
        IServiceScopeFactory scopes, IConfiguration configuration, ILogger<PlayCountReconciliationWorker> logger)
    {
        _scopes = scopes;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = _configuration.GetValue("PlayCounts:ReconciliationIntervalHours", 6.0);
        var interval = TimeSpan.FromHours(Math.Max(0.1, intervalHours));
        var autoRepair = _configuration.GetValue("PlayCounts:AutoRepair", false);
        var batchSize = _configuration.GetValue("PlayCounts:ReconciliationBatchSize", 500);

        _logger.LogInformation(
            "EVENT: PlayCountReconciliationWorkerStarted intervalHours:{Hours} autoRepair:{AutoRepair}",
            interval.TotalHours, autoRepair);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RunAsync(batchSize, autoRepair, stoppingToken);

        using var timer = new PeriodicTimer(interval);
        while (await WaitAsync(timer, stoppingToken))
        {
            await RunAsync(batchSize, autoRepair, stoppingToken);
        }
    }

    private async Task RunAsync(int batchSize, bool autoRepair, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var reconciliation = scope.ServiceProvider.GetRequiredService<IPlayCountReconciliationService>();

            var needsBackfill = await db.StreamSessions.AnyAsync(ct) && !await db.TrackStats.AnyAsync(ct);
            var repair = autoRepair || needsBackfill;

            var result = await reconciliation.ReconcileAsync(new ReconciliationOptions
            {
                DryRun = !repair,
                Repair = repair,
                BatchSize = batchSize,
                RequestedBy = "system:worker",
            }, ct);

            _logger.LogInformation(
                "EVENT: PlayCountReconciliationTick runId:{RunId} scanned:{Scanned} mismatches:{Mismatches} repaired:{Repaired} dryRun:{DryRun}",
                result.RunId, result.TracksScanned, result.MismatchesFound, result.MismatchesRepaired, result.DryRun);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EVENT: PlayCountReconciliationWorkerTickFailed");
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
}
