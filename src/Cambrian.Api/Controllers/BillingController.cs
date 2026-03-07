using System.Security.Claims;
using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("billing")]
[Authorize]
public class BillingController : BaseController
{
    private readonly ISubscriptionRepository _subscriptions;

    public BillingController(ISubscriptionRepository subscriptions)
    {
        _subscriptions = subscriptions;
    }

    [HttpPost("checkout")]
    public IActionResult Checkout(BillingCheckoutRequest request)
    {
        return OkResponse(new { success = true, paymentId = Guid.NewGuid().ToString(), invoiceId = Guid.NewGuid().ToString() });
    }

    [HttpPost("checkout-session")]
    public IActionResult CheckoutSession(BillingCheckoutRequest request)
    {
        return OkResponse(new { success = true, paymentId = Guid.NewGuid().ToString(), invoiceId = Guid.NewGuid().ToString() });
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var sub = await _subscriptions.GetActiveAsync(userId);
        return OkResponse(new { tier = sub?.Plan ?? "free", status = sub?.Status ?? "active" });
    }

    [HttpGet("checkout-session/{sessionId}")]
    public IActionResult GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return ErrorResponse("sessionId is required.");
        return OkResponse(new { sessionId });
    }
}
