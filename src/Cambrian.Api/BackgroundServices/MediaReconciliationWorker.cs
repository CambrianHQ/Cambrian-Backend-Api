using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Options;

namespace Cambrian.Api.BackgroundServices;

public sealed class MediaReconciliationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _interval;
    private readonly bool _remediate;
    private readonly ILogger<MediaReconciliationWorker> _logger;

    public MediaReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<PlaybackMediaOptions> options,
        ILogger<MediaReconciliationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _interval = TimeSpan.FromMinutes(options.Value.ReconciliationIntervalMinutes);
        _remediate = options.Value.ReconciliationWorkerRemediationEnabled;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await WaitAsync(timer, stoppingToken))
        {
            if (!MediaReconciliationRunGuard.TryAcquire())
            {
                _logger.LogInformation("Media reconciliation tick skipped because another run is already active");
                continue;
            }
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IMediaReconciliationService>();
                var result = await service.RunAsync(_remediate, stoppingToken);
                _logger.LogInformation(
                    "Media reconciliation {RunId} completed status={Status} findings={Findings} unresolvedPublished={UnresolvedPublished}",
                    result.RunId, result.Status, result.FindingCount, result.UnresolvedPublishedTrackFailures);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Media reconciliation worker tick failed");
            }
            finally
            {
                MediaReconciliationRunGuard.Release();
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
    }
}
