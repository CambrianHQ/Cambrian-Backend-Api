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
        var userId = GetRequiredUserId()!;
        var current = await _subscriptions.GetCurrentAsync(userId);
        return OkResponse(current);
    }

    [HttpPost("update")]
    public async Task<IActionResult> Update(UpdateSubscriptionRequest request)
    {
        var userId = GetRequiredUserId()!;

        // Paid plan upgrades must go through /billing/checkout → /billing/confirm
        // to ensure Stripe payment is verified before activation.
        if (request.Plan is "paid" or "creator")
        {
            // Allow re-sync when user is already on this plan (e.g., tier mismatch fix)
            var current = await _subscriptions.GetCurrentAsync(userId);
            if (current.Plan != request.Plan)
                return StatusCode(402, Common.ApiResponse.Fail("Paid plan upgrades require checkout. Use POST /billing/checkout."));
        }

        var result = await _subscriptions.UpdateAsync(request, userId);
        return OkResponse(result, "Subscription updated.");
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel()
    {
        var userId = GetRequiredUserId()!;

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
        var userId = GetRequiredUserId()!;
        var history = await _subscriptions.GetHistoryAsync(userId);
        return OkResponse(history);
    }
}
