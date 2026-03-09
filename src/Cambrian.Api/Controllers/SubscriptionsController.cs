using System.Security.Claims;
using Cambrian.Application.DTOs.Subscriptions;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("subscriptions")]
[Authorize]
public class SubscriptionsController : BaseController
{
    private readonly ISubscriptionService _subscriptions;

    public SubscriptionsController(ISubscriptionService subscriptions)
    {
        _subscriptions = subscriptions;
    }

    [AllowAnonymous]
    [HttpGet("plans")]
    public async Task<IActionResult> Plans()
    {
        var plans = await _subscriptions.GetPlansAsync();
        return OkResponse(plans);
    }

    [HttpGet("current")]
    public async Task<IActionResult> Current()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var sub = await _subscriptions.GetCurrentAsync(userId);

        return OkResponse(new
        {
            plan = sub.Plan,
            status = sub.Status,
            nextBilling = sub.ExpiresAt?.ToString("o") ?? ""
        });
    }

    [HttpPost("update")]
    public async Task<IActionResult> Update(UpdateSubscriptionRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var requestedPlan = request.Plan?.Trim().ToLowerInvariant() ?? "";

        if (string.IsNullOrWhiteSpace(requestedPlan))
            return ErrorResponse("Plan is required.");

        // Upgrades are fulfilled only after Stripe checkout + webhook confirmation.
        if (requestedPlan != "free")
            return ErrorResponse("Paid plan changes must be completed through billing checkout.");

        request.Plan = requestedPlan;
        var subscription = await _subscriptions.UpdateAsync(request, userId);
        return OkResponse(new { plan = subscription.Plan }, "Subscription updated.");
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        try
        {
            await _subscriptions.CancelAsync(userId);
            return MessageResponse("Subscription cancelled.");
        }
        catch (InvalidOperationException ex)
        {
            return ErrorResponse(ex.Message);
        }
    }

    [HttpGet("history")]
    public async Task<IActionResult> History()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var history = await _subscriptions.GetHistoryAsync(userId);
        return OkResponse(history);
    }
}
