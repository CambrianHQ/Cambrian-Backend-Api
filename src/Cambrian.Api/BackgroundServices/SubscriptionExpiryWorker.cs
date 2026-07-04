using Cambrian.Application.Interfaces;

namespace Cambrian.Api.BackgroundServices;

/// <summary>
/// Keeps subscription Status truthful: periodically flips 'active' → 'expired'
/// for subs past their ExpiresAt. Tier enforcement is already handled at read
/// time in SubscriptionRepository.GetActiveAsync (a lapsed sub never grants a
/// tier) — this sweep only keeps the stored Status accurate for admin views,
/// reporting, and any other Status=='active' query. Runs once at startup (to
/// clean up historical lapsed rows) and then hourly. Mirrors MasteringWorker:
/// PeriodicTimer, scoped per tick, never lets one tick kill the loop.
/// </summary>
public sealed class SubscriptionExpiryWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SubscriptionExpiryWorker> _logger;

    public SubscriptionExpiryWorker(IServiceScopeFactory scopes, ILogger<SubscriptionExpiryWorker> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EVENT: SubscriptionExpiryWorkerStarted intervalHours:{Hours}", Interval.TotalHours);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SweepAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (await WaitAsync(timer, stoppingToken))
        {
            await SweepAsync(stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var subs = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();
            var expired = await subs.ExpireLapsedAsync(DateTime.UtcNow);
            if (expired > 0)
                _logger.LogInformation("EVENT: SubscriptionsExpired count:{Count}", expired);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EVENT: SubscriptionExpirySweepFailed");
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
