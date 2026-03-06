using System.Security.Claims;
using Cambrian.Application.DTOs.Subscriptions;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("subscriptions")]
[Authorize]
public class SubscriptionsController : BaseController
{
    private readonly ISubscriptionRepository _subscriptions;

    public SubscriptionsController(ISubscriptionRepository subscriptions)
    {
        _subscriptions = subscriptions;
    }

    /// <summary>List available subscription plans.</summary>
    [AllowAnonymous]
    [HttpGet("plans")]
    public IActionResult Plans()
    {
        var plans = new List<PlanResponse>
        {
            new()
            {
                Name = "free",
                Description = "Basic access to the Cambrian marketplace.",
                PriceCents = 0,
                Interval = "month",
                Features = ["Browse catalog", "Stream tracks", "Purchase licenses"]
            },
            new()
            {
                Name = "paid",
                Description = "Enhanced listener experience with downloads and playlists.",
                PriceCents = 999,
                Interval = "month",
                Features = ["Everything in Free", "Unlimited downloads", "Offline listening", "Priority support"]
            },
            new()
            {
                Name = "creator",
                Description = "Full creator toolkit for uploading and selling beats.",
                PriceCents = 1999,
                Interval = "month",
                Features = ["Everything in Paid", "Upload tracks", "Sell licenses", "Analytics dashboard", "Payout access"]
            }
        };

        return OkResponse(plans);
    }

    /// <summary>Get the current user's active subscription.</summary>
    [HttpGet("current")]
    public async Task<IActionResult> Current()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var sub = await _subscriptions.GetActiveAsync(userId);

        if (sub is null)
        {
            return OkResponse(new SubscriptionResponse
            {
                Id = Guid.Empty,
                Plan = "free",
                Status = "active",
                StartedAt = DateTime.UtcNow
            });
        }

        return OkResponse(new SubscriptionResponse
        {
            Id = sub.Id,
            Plan = sub.Plan,
            Status = sub.Status,
            StartedAt = sub.StartedAt,
            ExpiresAt = sub.ExpiresAt
        });
    }

    /// <summary>Create or update the user's subscription plan.</summary>
    [HttpPost("update")]
    public async Task<IActionResult> Update(UpdateSubscriptionRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var existing = await _subscriptions.GetActiveAsync(userId);

        if (existing is not null)
        {
            if (existing.Plan == request.Plan)
                return ErrorResponse("You are already on this plan.");

            // Cancel the old subscription before creating the new one
            await _subscriptions.CancelAsync(existing.Id);
        }

        if (request.Plan == "free")
        {
            // Downgrading to free — no new subscription row needed
            return OkResponse(new SubscriptionResponse
            {
                Id = Guid.Empty,
                Plan = "free",
                Status = "active",
                StartedAt = DateTime.UtcNow
            });
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

        return OkResponse(new SubscriptionResponse
        {
            Id = subscription.Id,
            Plan = subscription.Plan,
            Status = subscription.Status,
            StartedAt = subscription.StartedAt,
            ExpiresAt = subscription.ExpiresAt
        }, "Subscription updated.");
    }

    /// <summary>Cancel the current subscription.</summary>
    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var existing = await _subscriptions.GetActiveAsync(userId);

        if (existing is null)
            return ErrorResponse("No active subscription to cancel.");

        await _subscriptions.CancelAsync(existing.Id);
        return MessageResponse("Subscription cancelled.");
    }

    /// <summary>Get the user's subscription history.</summary>
    [HttpGet("history")]
    public async Task<IActionResult> History()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var history = await _subscriptions.GetHistoryAsync(userId);

        var items = history.Select(s => new SubscriptionResponse
        {
            Id = s.Id,
            Plan = s.Plan,
            Status = s.Status,
            StartedAt = s.StartedAt,
            ExpiresAt = s.ExpiresAt
        });

        return OkResponse(items);
    }
}
