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

    public SubscriptionService(ISubscriptionRepository subscriptions, ITransactionManager transactions, UserManager<ApplicationUser> users)
    {
        _subscriptions = subscriptions;
        _transactions = transactions;
        _users = users;
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
                Name = "Paid",
                PriceCents = 499,
                Features = ["Everything in Free", "Unlimited downloads", "Offline listening", "Priority support"]
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
            ExpiresAt = sub?.ExpiresAt
        };
    }

    public async Task<SubscriptionResponse> UpdateAsync(UpdateSubscriptionRequest request, string userId)
    {
        var existing = await _subscriptions.GetActiveAsync(userId);
        var user = await _users.FindByIdAsync(userId);

        // Normalize "creator" -> "pro"
        var plan = request.Plan;
        if (string.Equals(plan, "creator", StringComparison.OrdinalIgnoreCase))
            plan = "pro";

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
                return new SubscriptionResponse { Plan = existing.Plan, Status = existing.Status };
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
                ExpiresAt = subscription.ExpiresAt
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

        await _subscriptions.CancelAsync(existing.Id);

        var user = await _users.FindByIdAsync(userId);
        if (user is not null)
        {
            user.Tier = "free";
            user.SubscriptionStatus = "Cancelled";
            SyncCreatorTier(user);
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
            ExpiresAt = s.ExpiresAt
        }).ToList();
    }

    /// <summary>
    /// Keeps the CreatorTier enum in sync with the Tier string field.
    /// Must be called whenever Tier changes.
    /// </summary>
    private static void SyncCreatorTier(ApplicationUser user)
    {
        user.CreatorTier = user.Tier switch
        {
            "pro" => CreatorTier.Pro,
            _ => CreatorTier.Free
        };
    }

}
