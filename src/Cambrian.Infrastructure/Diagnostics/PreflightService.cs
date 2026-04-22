using System.Diagnostics;
using Cambrian.Application.Interfaces;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Cambrian.Infrastructure.Diagnostics;

/// <summary>
/// Probes DB + Storage + Stripe in parallel and collapses the results into a
/// single structured verdict. The HTTP layer consumes <see cref="PreflightResult.Degraded"/>
/// to pick between 200 and 503 — it does not inspect individual checks.
/// </summary>
public sealed class PreflightService : IPreflightService
{
    private readonly CambrianDbContext _db;
    private readonly IObjectStorage _storage;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _env;
    private readonly ILogger<PreflightService> _logger;

    public PreflightService(
        CambrianDbContext db,
        IObjectStorage storage,
        IConfiguration config,
        IHostEnvironment env,
        ILogger<PreflightService> logger)
    {
        _db = db;
        _storage = storage;
        _config = config;
        _env = env;
        _logger = logger;
    }

    public async Task<PreflightResult> RunAsync(CancellationToken ct)
    {
        var dbTask = CheckDbAsync(ct);
        var storageTask = CheckStorageAsync();
        var stripeTask = CheckStripeAsync(ct);

        await Task.WhenAll(dbTask, storageTask, stripeTask);

        var db = await dbTask;
        var storage = await storageTask;
        var stripe = await stripeTask;

        var allOk = db.Ok && storage.Ok && stripe.Ok;

        if (!allOk)
        {
            _logger.LogError(
                "Preflight failed: db={DbOk} ({DbErr}) storage={StOk} ({StErr}) stripe={StrOk} ({StrErr})",
                db.Ok, db.Error, storage.Ok, storage.Error, stripe.Ok, stripe.Error);
        }

        return new PreflightResult
        {
            Degraded = !allOk,
            Body = new
            {
                status = allOk ? "ok" : "degraded",
                environment = _env.EnvironmentName,
                timestamp = DateTime.UtcNow,
                db = Project(db),
                storage = Project(storage),
                stripe = Project(stripe),
            },
        };
    }

    private async Task<CheckResult> CheckDbAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var canConnect = await _db.Database.CanConnectAsync(ct);
            if (!canConnect)
                return CheckResult.Fail(sw.ElapsedMilliseconds, "cannot connect to database");

            // A real query — CanConnectAsync only opens a connection; this
            // proves the schema is readable (catches "DB up but migration crashed").
            _ = await _db.Users.AsNoTracking().FirstOrDefaultAsync(ct);
            return CheckResult.Pass(sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return CheckResult.Fail(sw.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<CheckResult> CheckStorageAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var probe = await _storage.ProbeAsync();
            if (!probe.HeadBucketOk)
                return CheckResult.Fail(sw.ElapsedMilliseconds, probe.HeadBucketError ?? "bucket not reachable");
            return CheckResult.Pass(sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return CheckResult.Fail(sw.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<CheckResult> CheckStripeAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var secret = _config["Stripe:SecretKey"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            // In Production, startup refuses to boot without a Stripe key, so
            // reaching here without one means dev/staging with Stripe
            // intentionally unconfigured. Report skipped so local dev stays green.
            if (_env.IsProduction())
                return CheckResult.Fail(sw.ElapsedMilliseconds, "Stripe:SecretKey not configured in Production");
            return CheckResult.Skip(sw.ElapsedMilliseconds, "Stripe:SecretKey not configured");
        }

        try
        {
            var service = new BalanceService();
            var requestOptions = new RequestOptions { ApiKey = secret };
            _ = await service.GetAsync(requestOptions: requestOptions, cancellationToken: ct);
            return CheckResult.Pass(sw.ElapsedMilliseconds);
        }
        catch (StripeException ex)
        {
            return CheckResult.Fail(sw.ElapsedMilliseconds, $"Stripe API: {ex.Message}");
        }
        catch (Exception ex)
        {
            return CheckResult.Fail(sw.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static object Project(CheckResult r) => new
    {
        status = r.Status,
        latencyMs = r.LatencyMs,
        error = r.Error,
    };

    private readonly record struct CheckResult(bool Ok, string Status, long LatencyMs, string? Error)
    {
        public static CheckResult Pass(long ms) => new(true, "ok", ms, null);
        public static CheckResult Fail(long ms, string error) => new(false, "fail", ms, error);
        public static CheckResult Skip(long ms, string reason) => new(true, "skip", ms, reason);
    }
}
