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
        // Frontend expects: SubscriptionPlan[] = [{ id, name, priceMonthly, features }]
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
                priceMonthly = 9.99m,
                features = new[] { "Everything in Free", "Unlimited downloads", "Offline listening", "Priority support" }
            },
            new
            {
                id = "creator",
                name = "Creator",
                priceMonthly = 19.99m,
                features = new[] { "Everything in Paid", "Upload tracks", "Sell licenses", "Analytics dashboard", "Payout access" }
            }
        };

        return Ok(plans);
    }

    /// <summary>Get the current user's active subscription.</summary>
    [HttpGet("current")]
    public async Task<IActionResult> Current()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var sub = await _subscriptions.GetActiveAsync(userId);

        // Frontend expects: SubscriptionInfo { plan, status, nextBilling }
        return Ok(new
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

        if (existing is not null)
        {
            if (existing.Plan == request.Plan)
                return BadRequest(new { success = false, plan = existing.Plan, message = "You are already on this plan." });

            // Cancel the old subscription before creating the new one
            await _subscriptions.CancelAsync(existing.Id);
        }

        if (request.Plan == "free")
        {
            // Downgrading to free — no new subscription row needed
            return Ok(new { success = true, plan = "free", message = "Downgraded to free plan." });
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

        // Frontend expects: { success, plan, message }
        return Ok(new { success = true, plan = subscription.Plan, message = "Subscription updated." });
    }

    /// <summary>Cancel the current subscription.</summary>
    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var existing = await _subscriptions.GetActiveAsync(userId);

        if (existing is null)
            return BadRequest(new { success = false, message = "No active subscription to cancel." });

        await _subscriptions.CancelAsync(existing.Id);
        return Ok(new { success = true, message = "Subscription cancelled." });
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

        return Ok(items);
    }
}
