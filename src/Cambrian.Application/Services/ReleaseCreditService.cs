using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.ReleaseReady;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

/// <summary>
/// Release Ready credits. Two pools, both DERIVED (no stored balance to drift):
/// <list type="bullet">
/// <item><b>Monthly</b> — a per-tier config allowance
/// (<see cref="TierConfig.ReleaseReadyCreditsPerMonth"/>); usage = jobs charged this
/// calendar month from the monthly source. Resets each cycle by construction.</item>
/// <item><b>Purchased</b> — one-time packs that never expire; remaining =
/// SUM(paid credits) − jobs charged from the purchased source.</item>
/// </list>
/// Monthly credits spend first; the purchased pool only funds a charge once the
/// month's allowance is exhausted.
/// </summary>
public sealed class ReleaseCreditService : IReleaseCreditService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly IMasteringJobRepository _jobs;
    private readonly IReleaseCreditPurchaseRepository _purchases;
    private readonly IPaymentGateway _gateway;
    private readonly IConfiguration _config;
    private readonly ITransactionManager _transactions;
    private readonly TimeProvider _time;
    private readonly ILogger<ReleaseCreditService> _logger;

    public ReleaseCreditService(
        UserManager<ApplicationUser> users,
        IMasteringJobRepository jobs,
        IReleaseCreditPurchaseRepository purchases,
        IPaymentGateway gateway,
        IConfiguration config,
        ITransactionManager transactions,
        TimeProvider time,
        ILogger<ReleaseCreditService> logger)
    {
        _users = users;
        _jobs = jobs;
        _purchases = purchases;
        _gateway = gateway;
        _config = config;
        _transactions = transactions;
        _time = time;
        _logger = logger;
    }

    public async Task<CreditStatusDto> GetStatusAsync(string userId, CancellationToken ct = default)
    {
        var tier = await ResolveTierAsync(userId);
        var allowance = tier.ReleaseReadyCreditsPerMonth;
        var monthStart = MonthStartUtc();

        var monthlyUsed = await _jobs.CountChargedThisMonthAsync(userId, monthStart, ct);
        var monthlyRemaining = Math.Max(0, allowance - monthlyUsed);

        var purchasedTotal = await _purchases.SumPaidCreditsAsync(userId, ct);
        var purchasedUsed = await _jobs.CountPurchasedConsumedAsync(userId, ct);
        var purchasedRemaining = Math.Max(0, purchasedTotal - purchasedUsed);

        return new CreditStatusDto
        {
            Allowance = allowance,
            Used = monthlyUsed,
            Remaining = monthlyRemaining + purchasedRemaining,
            Plan = tier.Slug,
            MonthlyRemaining = monthlyRemaining,
            Purchased = purchasedRemaining,
            ResetsAt = monthStart.AddMonths(1),
        };
    }

    /// <summary>
    /// Atomic count-and-charge in ONE serializable transaction. Re-fetches the job
    /// (already-charged → idempotent true), then spends a monthly credit if any remain,
    /// otherwise a purchased credit. Serializable isolation prevents two concurrent
    /// submits from both passing on the last credit of either pool.
    /// </summary>
    public async Task<bool> TryChargeAsync(Guid jobId, string userId, CancellationToken ct = default)
    {
        var tier = await ResolveTierAsync(userId);
        var allowance = tier.ReleaseReadyCreditsPerMonth;

        await using var tx = await _transactions.BeginSerializableTransactionAsync();
        try
        {
            var job = await _jobs.GetForOwnerAsync(jobId, userId, ct);
            if (job is null)
            {
                await _transactions.RollbackAsync();
                throw new KeyNotFoundException($"Mastering job {jobId} not found.");
            }

            // Idempotent: a job already charged consumes no extra credit.
            if (job.ChargedAt is not null)
            {
                await _transactions.CommitAsync();
                return true;
            }

            // Monthly credits spend first; fall back to the never-expiring purchased pool.
            var monthlyUsed = await _jobs.CountChargedThisMonthAsync(userId, MonthStartUtc(), ct);
            string source;
            if (monthlyUsed < allowance)
            {
                source = "monthly";
            }
            else
            {
                var purchasedTotal = await _purchases.SumPaidCreditsAsync(userId, ct);
                var purchasedUsed = await _jobs.CountPurchasedConsumedAsync(userId, ct);
                if (purchasedTotal - purchasedUsed > 0)
                {
                    source = "purchased";
                }
                else
                {
                    await _transactions.RollbackAsync();
                    _logger.LogInformation(
                        "EVENT: ReleaseReadyChargeDenied userId:{UserId} jobId:{JobId} monthlyUsed:{Used} allowance:{Allowance} purchasedRemaining:0",
                        userId, jobId, monthlyUsed, allowance);
                    return false;
                }
            }

            job.ChargedAt = _time.GetUtcNow().UtcDateTime;
            job.CreditSource = source;
            await _jobs.UpdateAsync(job, ct);
            await _transactions.CommitAsync();

            _logger.LogInformation(
                "EVENT: ReleaseReadyCharged userId:{UserId} jobId:{JobId} source:{Source} monthlyUsed:{Used} allowance:{Allowance}",
                userId, jobId, source, monthlyUsed, allowance);
            return true;
        }
        catch (KeyNotFoundException)
        {
            throw;
        }
        catch
        {
            await _transactions.RollbackAsync();
            throw;
        }
    }

    public async Task<CreditCheckoutResponse> CreateCreditCheckoutAsync(string userId, string packId, CancellationToken ct = default)
    {
        var pack = CreditPackCatalog.Find(packId)
            ?? throw new InvalidOperationException($"Unknown credit pack '{packId}'.");

        var user = await _users.FindByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found.");

        var frontendUrl = (_config["App:FrontendUrl"] ?? "").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(frontendUrl))
            throw new InvalidOperationException("App:FrontendUrl must be configured for credit checkout.");

        var label = pack.Credits == 1 ? "1 Release Ready credit" : $"{pack.Credits} Release Ready credits";

        // clientReferenceId carries the SERVER-resolved credit count; the webhook grants it.
        var checkoutUrl = await _gateway.CreateCheckoutSessionAsync(
            pack.PriceCents,
            $"Cambrian — {label}",
            clientReferenceId: $"{userId}:credits:{pack.Credits}",
            successUrl: $"{frontendUrl}/studio/credits?purchased={pack.Credits}",
            cancelUrl: $"{frontendUrl}/studio/credits?cancelled=true",
            customerEmail: user.Email);

        _logger.LogInformation(
            "EVENT: ReleaseCreditCheckoutStarted userId:{UserId} pack:{Pack} credits:{Credits} cents:{Cents}",
            userId, pack.Id, pack.Credits, pack.PriceCents);

        return new CreditCheckoutResponse { CheckoutUrl = checkoutUrl };
    }

    private async Task<TierConfig> ResolveTierAsync(string userId)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null)
            throw new KeyNotFoundException($"User {userId} not found.");

        // CreatorTier is the authoritative entitlement (kept in sync by the Stripe webhook).
        return TierManifest.For(user.CreatorTier);
    }

    // Calendar-month boundary in UTC, derived from the injected clock so month
    // rollover is deterministically testable (the #1 reason credit logic must not
    // read system time directly).
    private DateTime MonthStartUtc()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}
