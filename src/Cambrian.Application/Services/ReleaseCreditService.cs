using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.ReleaseReady;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

/// <summary>
/// Release Ready monthly credits. The allowance is a per-tier config constant
/// (<see cref="TierConfig.ReleaseReadyCreditsPerMonth"/>); usage is derived from
/// <see cref="MasteringJob.ChargedAt"/> within the current calendar month UTC, so
/// there is no stored balance to drift and no rollover by construction.
/// </summary>
public sealed class ReleaseCreditService : IReleaseCreditService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly IMasteringJobRepository _jobs;
    private readonly ITransactionManager _transactions;
    private readonly TimeProvider _time;
    private readonly ILogger<ReleaseCreditService> _logger;

    public ReleaseCreditService(
        UserManager<ApplicationUser> users,
        IMasteringJobRepository jobs,
        ITransactionManager transactions,
        TimeProvider time,
        ILogger<ReleaseCreditService> logger)
    {
        _users = users;
        _jobs = jobs;
        _transactions = transactions;
        _time = time;
        _logger = logger;
    }

    public async Task<CreditStatusDto> GetStatusAsync(string userId, CancellationToken ct = default)
    {
        var tier = await ResolveTierAsync(userId);
        var allowance = tier.ReleaseReadyCreditsPerMonth;
        var used = await _jobs.CountChargedThisMonthAsync(userId, MonthStartUtc(), ct);
        var remaining = Math.Max(0, allowance - used);

        return new CreditStatusDto
        {
            Allowance = allowance,
            Used = used,
            Remaining = remaining,
            Plan = tier.Slug,
        };
    }

    /// <summary>
    /// Atomic count-and-charge in ONE serializable transaction. Re-fetches the job
    /// (already-charged → idempotent true), counts the month's charged jobs, and only
    /// sets <see cref="MasteringJob.ChargedAt"/> when a credit remains. Serializable
    /// isolation prevents two concurrent submits from both passing with one credit left.
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

            // Idempotent: a job already charged this month consumes no extra credit.
            if (job.ChargedAt is not null)
            {
                await _transactions.CommitAsync();
                return true;
            }

            var used = await _jobs.CountChargedThisMonthAsync(userId, MonthStartUtc(), ct);
            if (used >= allowance)
            {
                await _transactions.RollbackAsync();
                _logger.LogInformation(
                    "EVENT: ReleaseReadyChargeDenied userId:{UserId} jobId:{JobId} used:{Used} allowance:{Allowance}",
                    userId, jobId, used, allowance);
                return false;
            }

            job.ChargedAt = _time.GetUtcNow().UtcDateTime;
            await _jobs.UpdateAsync(job, ct);
            await _transactions.CommitAsync();

            _logger.LogInformation(
                "EVENT: ReleaseReadyCharged userId:{UserId} jobId:{JobId} used:{Used} allowance:{Allowance}",
                userId, jobId, used + 1, allowance);
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
