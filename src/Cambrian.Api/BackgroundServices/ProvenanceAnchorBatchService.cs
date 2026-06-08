using Cambrian.Application.Configuration;
using Cambrian.Application.Services;
using Microsoft.Extensions.Options;

namespace Cambrian.Api.BackgroundServices;

/// <summary>
/// Scheduled worker that anchors pending provenance batches on-chain. Each tick opens a DI scope and
/// runs one <see cref="ProvenanceAnchorBatchProcessor.ProcessBatchAsync"/>. Registered only when
/// <c>Provenance:Anchor:JobEnabled</c> is true, so it never runs in tests or in environments that
/// haven't opted in with a real anchor configured.
///
/// <para>Assumes a single worker instance (the work-list is the <c>pending</c> rows). Running multiple
/// app instances could double-anchor a batch — harmless to data but wasteful of gas; add a leader lock
/// (e.g. <c>pg_try_advisory_lock</c>) or pin the worker to one instance if you scale out.</para>
/// </summary>
public sealed class ProvenanceAnchorBatchService : BackgroundService
{
    private const int MinIntervalSeconds = 15;

    private readonly IServiceScopeFactory _scopes;
    private readonly ProvenanceAnchorOptions _options;
    private readonly ILogger<ProvenanceAnchorBatchService> _logger;

    public ProvenanceAnchorBatchService(
        IServiceScopeFactory scopes,
        IOptions<ProvenanceAnchorOptions> options,
        ILogger<ProvenanceAnchorBatchService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(MinIntervalSeconds, _options.IntervalSeconds));
        _logger.LogInformation(
            "EVENT: ProvenanceAnchorWorkerStarted provider:{Provider} intervalSeconds:{Interval} maxBatch:{Max}",
            _options.Provider, interval.TotalSeconds, _options.MaxBatchSize);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<ProvenanceAnchorBatchProcessor>();
                await processor.ProcessBatchAsync(_options.MaxBatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutting down
            }
            catch (Exception ex)
            {
                // Never let a tick kill the loop.
                _logger.LogError(ex, "EVENT: ProvenanceAnchorWorkerTickFailed");
            }
        }
    }
}
