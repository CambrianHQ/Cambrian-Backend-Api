using Cambrian.Application.Interfaces;

namespace Cambrian.Api.BackgroundServices;

/// <summary>
/// Scheduler for the weekly creator digest. Ticks hourly; fires once the
/// configured send point (day + UTC hour) inside the current week has passed.
/// Double-fire safety lives in the SERVICE (per-user LastWeeklyDigestAtUtc
/// stamp), so restarts and manual runs are harmless.
///
/// Config (Digest section):
///   Enabled — master switch, default FALSE (deploying never spontaneously emails).
///   DryRun  — default TRUE; logs recipients + stats without sending.
///   SendDayUtc / SendHourUtc — default Monday 15:00 UTC.
/// </summary>
public sealed class WeeklyDigestWorker : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly IWeeklyDigestService _digest;
    private readonly IConfiguration _config;
    private readonly ILogger<WeeklyDigestWorker> _logger;

    public WeeklyDigestWorker(IWeeklyDigestService digest, IConfiguration config, ILogger<WeeklyDigestWorker> logger)
    {
        _digest = digest;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dryRun = _config.GetValue("Digest:DryRun", true);
        _logger.LogInformation("EVENT: WeeklyDigestWorkerStarted dryRun:{DryRun}", dryRun);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try
            {
                if (SendPointHasPassed(DateTime.UtcNow))
                {
                    // Per-user idempotence in the service makes repeated calls
                    // within the same week no-ops for already-stamped users.
                    await _digest.RunAsync(_config.GetValue("Digest:DryRun", true), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EVENT: WeeklyDigestTickFailed");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private bool SendPointHasPassed(DateTime nowUtc)
    {
        var sendDay = _config.GetValue("Digest:SendDayUtc", DayOfWeek.Monday);
        var sendHour = Math.Clamp(_config.GetValue("Digest:SendHourUtc", 15), 0, 23);

        var weekStart = nowUtc.Date.AddDays(-((7 + (int)nowUtc.DayOfWeek - (int)DayOfWeek.Monday) % 7));
        var sendPoint = weekStart.AddDays(((int)sendDay - (int)DayOfWeek.Monday + 7) % 7).AddHours(sendHour);
        return nowUtc >= sendPoint;
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
