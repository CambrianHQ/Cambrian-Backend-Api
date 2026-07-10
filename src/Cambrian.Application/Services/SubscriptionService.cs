using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Subscriptions;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Microsoft.AspNetCore.Identity;

namespace Cambrian.Application.Services;

public class SubscriptionService : ISubscriptionService
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly ITransactionManager _transactions;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IPaymentGateway _gateway;

    public SubscriptionService(ISubscriptionRepository subscriptions, ITransactionManager transactions, UserManager<ApplicationUser> users, IPaymentGateway gateway)
    {
        _subscriptions = subscriptions;
        _transactions = transactions;
        _users = users;
        _gateway = gateway;
    }

    public Task<IReadOnlyCollection<PlanResponse>> GetPlansAsync()
    {
        IReadOnlyCollection<PlanResponse> plans = new List<PlanResponse>
        {
            new()
            {
                Name = "Free",
                PriceCents = 0,
                Features = ["Browse catalog", "Stream tracks", "Purchase licenses"]
            },
            new()
            {
                Name = "Buyer",
                PriceCents = 999,
                Features = ["Purchase track licenses", "Download purchased tracks", "PDF license certificates", "Personal and commercial use"]
            },
            new()
            {
                Name = TierManifest.Pro.DisplayName,
                PriceCents = TierManifest.Pro.PriceCents,
                Features = TierManifest.Pro.Features.ToList()
            }
        };

        return Task.FromResult(plans);
    }

    public async Task<SubscriptionResponse> GetCurrentAsync(string userId)
    {
        var sub = await _subscriptions.GetActiveAsync(userId);

        return new SubscriptionResponse
        {
            Id = sub?.Id ?? Guid.Empty,
            Plan = sub?.Plan ?? "free",
            Status = sub?.Status ?? "active",
            StartedAt = sub?.StartedAt ?? default,
            ExpiresAt = sub?.ExpiresAt,
            TrialEndsAt = sub?.TrialEndsAt
        };
    }

    public async Task<SubscriptionResponse> UpdateAsync(UpdateSubscriptionRequest request, string userId)
    {
        var existing = await _subscriptions.GetActiveAsync(userId);
        var user = await _users.FindByIdAsync(userId);

        // Normalize to a known tier slug (free/creator/pro). "paid" is a legacy buyer plan
        // and is preserved verbatim (it is not part of the creator tier matrix).
        var plan = (request.Plan ?? "").Trim().ToLowerInvariant();
        if (plan is not ("free" or "creator" or "pro" or "paid"))
            plan = TierManifest.For(plan).Slug;

        if (existing is not null)
        {
            if (existing.Plan == plan)
            {
                if (user is not null && user.Tier != plan)
                {
                    user.Tier = plan;
                    SyncCreatorTier(user);
                    await _users.UpdateAsync(user);
                }
                return new SubscriptionResponse
                {
                    Plan = existing.Plan,
                    Status = existing.Status,
                    TrialEndsAt = existing.TrialEndsAt,
                    ExpiresAt = existing.ExpiresAt
                };
            }
        }

        // Wrap cancel + create + user tier update in a transaction
        await using var txHandle = await _transactions.BeginTransactionAsync();
        try
        {
            if (existing is not null)
                await _subscriptions.CancelAsync(existing.Id);

            if (plan == "free")
            {
                if (user is not null)
                {
                    user.Tier = "free";
                    SyncCreatorTier(user);
                    await _users.UpdateAsync(user);
                }
                await _transactions.CommitAsync();
                return new SubscriptionResponse { Plan = "free", Status = "active" };
            }

            var subscription = new Subscription
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Plan = plan,
                Status = "active",
                StartedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMonths(1)
            };

            await _subscriptions.CreateAsync(subscription);

            if (user is not null)
            {
                user.Tier = plan;
                user.SubscriptionStatus = "Active";
                user.SubscriptionEndDate = subscription.ExpiresAt;
                SyncCreatorTier(user);
                await _users.UpdateAsync(user);
            }

            await _transactions.CommitAsync();

            return new SubscriptionResponse
            {
                Id = subscription.Id,
                Plan = subscription.Plan,
                Status = subscription.Status,
                StartedAt = subscription.StartedAt,
                ExpiresAt = subscription.ExpiresAt,
                TrialEndsAt = subscription.TrialEndsAt
            };
        }
        catch
        {
            await _transactions.RollbackAsync();
            throw;
        }
    }

    public async Task CancelAsync(string userId)
    {
        var existing = await _subscriptions.GetActiveAsync(userId);
        if (existing is null)
            throw new InvalidOperationException("No active subscription to cancel.");

        // Cancel at PERIOD END, not immediately. Two bugs this fixes:
        //  1. The old path never called Stripe at all, so a "cancelled" subscriber
        //     kept getting charged every month forever.
        //  2. It immediately set Tier = "free", stripping paid access (API, credits,
        //     lower fee, unlimited) the instant they cancelled — even though they had
        //     already paid through the end of the current period.
        // Now: schedule Stripe cancel_at_period_end, keep the subscriber on their tier
        // and their local subscription active until ExpiresAt, and let the existing
        // customer.subscription.deleted webhook do the downgrade when the period ends.
        DateTime? periodEnd = null;
        if (!string.IsNullOrWhiteSpace(existing.StripeSubscriptionId))
        {
            periodEnd = await _gateway.CancelSubscriptionAtPeriodEndAsync(existing.StripeSubscriptionId);
        }

        // Keep the row active so it still grants the tier; set ExpiresAt to the paid-through
        // date as a backstop for the webhook (ExpireLapsedAsync will fail it closed at period end).
        existing.ExpiresAt = periodEnd ?? existing.ExpiresAt;
        await _subscriptions.UpdateAsync(existing);

        var user = await _users.FindByIdAsync(userId);
        if (user is not null)
        {
            // Tier is intentionally NOT changed here — access continues until the
            // period-end webhook. Only mark that a cancellation is pending.
            user.SubscriptionStatus = "Cancelling";
            await _users.UpdateAsync(user);
        }
    }

    public async Task<IReadOnlyCollection<SubscriptionResponse>> GetHistoryAsync(string userId)
    {
        var history = await _subscriptions.GetHistoryAsync(userId);

        return history.Select(s => new SubscriptionResponse
        {
            Id = s.Id,
            Plan = s.Plan,
            Status = s.Status,
            StartedAt = s.StartedAt,
            ExpiresAt = s.ExpiresAt,
            TrialEndsAt = s.TrialEndsAt
        }).ToList();
    }

    /// <summary>
    /// Keeps the CreatorTier enum in sync with the Tier string field.
    /// Must be called whenever Tier changes.
    /// </summary>
    private static void SyncCreatorTier(ApplicationUser user)
    {
        // "paid" is a buyer plan with no creator tier — treat as Free for creator capabilities.
        user.CreatorTier = TierManifest.For(user.Tier ?? "free").Tier;
    }

}
