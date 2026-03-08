using System.Security.Claims;
using Cambrian.Application.DTOs.Subscriptions;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("subscriptions")]
[Authorize]
public class SubscriptionsController : BaseController
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly UserManager<ApplicationUser> _users;

    public SubscriptionsController(ISubscriptionRepository subscriptions, UserManager<ApplicationUser> users)
    {
        _subscriptions = subscriptions;
        _users = users;
    }

    /// <summary>List available subscription plans.</summary>
    [AllowAnonymous]
    [HttpGet("plans")]
    public IActionResult Plans()
    {
        var plans = new[]
        {
            new
            {
                id = "free",
                name = "Free",
                priceMonthly = 0.00m,
                features = new[] { "Browse catalog", "Stream tracks", "Purchase licenses" }
            },
            new
            {
                id = "paid",
                name = "Paid",
                priceMonthly = 4.99m,
                features = new[] { "Everything in Free", "Unlimited downloads", "Offline listening", "Priority support" }
            },
            new
            {
                id = "creator",
                name = "Creator",
                priceMonthly = 9.99m,
                features = new[] { "Everything in Paid", "Upload tracks", "Sell licenses", "Analytics dashboard", "Payout access" }
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

        return OkResponse(new
        {
            plan = sub?.Plan ?? "free",
            status = sub?.Status ?? "active",
            nextBilling = sub?.ExpiresAt?.ToString("o") ?? ""
        });
    }

    /// <summary>Create or update the user's subscription plan.</summary>
    [HttpPost("update")]
    public async Task<IActionResult> Update(UpdateSubscriptionRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var existing = await _subscriptions.GetActiveAsync(userId);

        // Look up the user so we can update the Tier field
        var user = await _users.FindByIdAsync(userId);

        if (existing is not null)
        {
            if (existing.Plan == request.Plan)
            {
                // Subscription already matches — but make sure the user's Tier
                // field is in sync (it may have been missed on a prior update).
                if (user is not null && user.Tier != request.Plan)
                {
                    user.Tier = request.Plan;
                    await _users.UpdateAsync(user);
                }
                return OkResponse(new { plan = existing.Plan }, "Subscription confirmed.");
            }

            // Cancel the old subscription before creating the new one
            await _subscriptions.CancelAsync(existing.Id);
        }

        if (request.Plan == "free")
        {
            // Downgrading to free — no new subscription row needed
            if (user is not null) { user.Tier = "free"; await _users.UpdateAsync(user); }
            return OkResponse(new { plan = "free" }, "Downgraded to free plan.");
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

        // Keep the user entity's Tier in sync
        if (user is not null) { user.Tier = request.Plan; await _users.UpdateAsync(user); }

        return OkResponse(new { plan = subscription.Plan }, "Subscription updated.");
    }

    /// <summary>Cancel the current subscription and reset tier to free.</summary>
    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var existing = await _subscriptions.GetActiveAsync(userId);

        if (existing is null)
            return ErrorResponse("No active subscription to cancel.");

        await _subscriptions.CancelAsync(existing.Id);

        // Reset the user's tier back to free
        var user = await _users.FindByIdAsync(userId);
        if (user is not null)
        {
            user.Tier = "free";
            await _users.UpdateAsync(user);
        }

        return MessageResponse("Subscription cancelled.");
    }

    /// <summary>Get the user's subscription history.</summary>
    [HttpGet("history")]
    public async Task<IActionResult> History()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var history = await _subscriptions.GetHistoryAsync(userId);

        var items = history.Select(s => new
        {
            id = s.Id,
            plan = s.Plan,
            status = s.Status,
            startedAt = s.StartedAt.ToString("o"),
            expiresAt = s.ExpiresAt?.ToString("o")
        });

        return OkResponse(items);
    }
}
