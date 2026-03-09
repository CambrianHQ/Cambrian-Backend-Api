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

    /// <summary>List available subscription plans.</summary>
    [HttpGet("plans")]
    public IActionResult Plans()
    {
        return OkResponse(_subscriptions.GetPlans());
    }

    /// <summary>Get the current user's active subscription.</summary>
    [HttpGet("current")]
    public async Task<IActionResult> Current()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        return OkResponse(await _subscriptions.GetCurrentAsync(userId));
    }

    /// <summary>Create or update the user's subscription plan.</summary>
    [HttpPost("update")]
    public async Task<IActionResult> Update(UpdateSubscriptionRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        return OkResponse(await _subscriptions.UpdateAsync(userId, request), "Subscription updated.");
    }

    /// <summary>Cancel the current subscription and reset tier to free.</summary>
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

    /// <summary>Get the user's subscription history.</summary>
    [HttpGet("history")]
    public async Task<IActionResult> History()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        return OkResponse(await _subscriptions.GetHistoryAsync(userId));
    }
}
