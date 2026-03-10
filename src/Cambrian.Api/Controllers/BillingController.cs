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
    private readonly IBillingService _billing;

    public BillingController(IBillingService billing)
    {
        _billing = billing;
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout(BillingCheckoutRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        try
        {
            var result = await _billing.CreateCheckoutAsync(request, userId);
            return OkResponse(new { url = result.CheckoutUrl });
        }
        catch (ArgumentException ex)
        {
            return ErrorResponse(ex.Message);
        }
    }

    [HttpPost("checkout-session")]
    public async Task<IActionResult> CheckoutSession(BillingCheckoutRequest request)
    {
        return await Checkout(request);
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var status = await _billing.GetStatusAsync(userId);
        return OkResponse(status);
    }

    [HttpGet("checkout-session/{sessionId}")]
    public IActionResult GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return ErrorResponse("sessionId is required.");
        return OkResponse(new { sessionId });
    }
}
