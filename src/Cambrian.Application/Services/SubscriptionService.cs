using Cambrian.Application.DTOs.Subscriptions;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace Cambrian.Application.Services;

public sealed class SubscriptionService : ISubscriptionService
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly UserManager<ApplicationUser> _users;

    private static readonly List<PlanResponse> Plans =
    [
        new()
        {
            Name = "Free",
            Description = "free",
            PriceCents = 0,
            Features = ["Browse catalog", "Stream tracks", "Purchase licenses"]
        },
        new()
        {
            Name = "Paid",
            Description = "paid",
            PriceCents = 499,
            Features = ["Everything in Free", "Unlimited downloads", "Offline listening", "Priority support"]
        },
        new()
        {
            Name = "Creator",
            Description = "creator",
            PriceCents = 999,
            Features = ["Everything in Paid", "Upload tracks", "Sell licenses", "Analytics dashboard", "Payout access"]
        }
    ];

    public SubscriptionService(ISubscriptionRepository subscriptions, UserManager<ApplicationUser> users)
    {
        _subscriptions = subscriptions;
        _users = users;
    }

    public Task<List<PlanResponse>> GetPlansAsync() => Task.FromResult(Plans);

    public async Task<SubscriptionResponse> GetCurrentAsync(string userId)
    {
        var sub = await _subscriptions.GetActiveAsync(userId);
        return new SubscriptionResponse
        {
            Id = sub?.Id ?? Guid.Empty,
            Plan = sub?.Plan ?? "free",
            Status = sub?.Status ?? "active",
            StartedAt = sub?.StartedAt ?? DateTime.MinValue,
            ExpiresAt = sub?.ExpiresAt
        };
    }

    public async Task<SubscriptionResponse> UpdateAsync(string plan, string userId)
    {
        var existing = await _subscriptions.GetActiveAsync(userId);
        var user = await _users.FindByIdAsync(userId);

        if (existing is not null)
        {
            if (existing.Plan == plan)
            {
                if (user is not null && user.Tier != plan)
                {
                    user.Tier = plan;
                    await _users.UpdateAsync(user);
                }
                return new SubscriptionResponse
                {
                    Id = existing.Id, Plan = existing.Plan,
                    Status = existing.Status, StartedAt = existing.StartedAt,
                    ExpiresAt = existing.ExpiresAt
                };
            }
            await _subscriptions.CancelAsync(existing.Id);
        }

        if (plan == "free")
        {
            if (user is not null) { user.Tier = "free"; await _users.UpdateAsync(user); }
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

        if (user is not null) { user.Tier = plan; await _users.UpdateAsync(user); }

        return new SubscriptionResponse
        {
            Id = subscription.Id, Plan = subscription.Plan,
            Status = subscription.Status, StartedAt = subscription.StartedAt,
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

    public async Task<List<SubscriptionResponse>> GetHistoryAsync(string userId)
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
