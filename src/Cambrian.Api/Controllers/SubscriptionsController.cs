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

    [AllowAnonymous]
    [HttpGet("plans")]
    public IActionResult Plans()
    {
        var plans = new[]
        {
            new PlanResponse
            {
                Name = "Free", PriceCents = 0,
                Features = ["Browse catalog", "Stream tracks", "Purchase licenses"]
            },
            new PlanResponse
            {
                Name = "Paid", PriceCents = 499,
                Features = ["Everything in Free", "Unlimited downloads", "Offline listening", "Priority support"]
            },
            new PlanResponse
            {
                Name = "Creator", PriceCents = 999,
                Features = ["Everything in Paid", "Upload tracks", "Sell licenses", "Analytics dashboard", "Payout access"]
            }
        };

        return OkResponse(plans);
    }

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

    [HttpPost("update")]
    public async Task<IActionResult> Update(UpdateSubscriptionRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var existing = await _subscriptions.GetActiveAsync(userId);
        var user = await _users.FindByIdAsync(userId);

        if (existing is not null && existing.Plan == request.Plan)
        {
            if (user is not null && user.Tier != request.Plan)
            {
                user.Tier = request.Plan;
                await _users.UpdateAsync(user);
            }
            return OkResponse(new { plan = existing.Plan }, "Subscription updated.");
        }

        if (existing is not null)
            await _subscriptions.CancelAsync(existing.Id);

        if (request.Plan == "free")
        {
            if (user is not null) { user.Tier = "free"; await _users.UpdateAsync(user); }
            return OkResponse(new { plan = "free" }, "Subscription updated.");
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

        return OkResponse(new { plan = subscription.Plan }, "Subscription updated.");
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var existing = await _subscriptions.GetActiveAsync(userId);

        if (existing is null)
            return ErrorResponse("No active subscription to cancel.");

        await _subscriptions.CancelAsync(existing.Id);

        var user = await _users.FindByIdAsync(userId);
        if (user is not null)
        {
            user.Tier = "free";
            await _users.UpdateAsync(user);
        }

        return MessageResponse("Subscription cancelled.");
    }

    [HttpGet("history")]
    public async Task<IActionResult> History()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var history = await _subscriptions.GetHistoryAsync(userId);
        return OkResponse(history);
    }
}
