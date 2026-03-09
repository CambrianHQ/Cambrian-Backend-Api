using Cambrian.Application.DTOs.Subscriptions;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace Cambrian.Application.Services;

public class SubscriptionService : ISubscriptionService
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly UserManager<ApplicationUser> _users;

    public SubscriptionService(ISubscriptionRepository subscriptions, UserManager<ApplicationUser> users)
    {
        _subscriptions = subscriptions;
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
                Name = "Creator",
                PriceCents = 999,
                Features = ["Everything in Paid", "Upload tracks", "Sell licenses", "Analytics dashboard", "Payout access"]
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
        var requestedPlan = request.Plan?.Trim().ToLowerInvariant() ?? "";
        if (requestedPlan != "free")
            throw new InvalidOperationException("Paid plans must be activated through Stripe checkout.");

        var existing = await _subscriptions.GetActiveAsync(userId);
        var user = await _users.FindByIdAsync(userId);

        if (existing is not null)
        {
            if (existing.Plan == requestedPlan)
            {
                if (user is not null && user.Tier != requestedPlan)
                {
                    user.Tier = requestedPlan;
                    await _users.UpdateAsync(user);
                }
                return new SubscriptionResponse { Plan = existing.Plan, Status = existing.Status };
            }

            await _subscriptions.CancelAsync(existing.Id);
        }

        if (requestedPlan == "free")
        {
            if (user is not null) { user.Tier = "free"; await _users.UpdateAsync(user); }
            return new SubscriptionResponse { Plan = "free", Status = "active" };
        }

        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Plan = request.Plan,
            Status = "active",
            StartedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMonths(1)
        };

        await _subscriptions.CreateAsync(subscription);

        if (user is not null) { user.Tier = request.Plan; await _users.UpdateAsync(user); }

        return new SubscriptionResponse
        {
            Id = subscription.Id,
            Plan = subscription.Plan,
            Status = subscription.Status,
            StartedAt = subscription.StartedAt,
            ExpiresAt = subscription.ExpiresAt
        };
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
}
