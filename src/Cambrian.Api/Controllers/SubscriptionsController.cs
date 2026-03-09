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
        var current = await _subscriptions.GetCurrentAsync(userId);

        return OkResponse(new
        {
            plan = current.Plan,
            status = current.Status,
            nextBilling = current.ExpiresAt?.ToString("o") ?? ""
        });
    }

    [HttpPost("update")]
    public async Task<IActionResult> Update(UpdateSubscriptionRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await _subscriptions.UpdateAsync(request, userId);
        return OkResponse(new { plan = result.Plan }, "Subscription updated.");
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        try
        {
            await _subscriptions.CancelAsync(userId);
        }
        catch (InvalidOperationException ex)
        {
            return ErrorResponse(ex.Message);
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
