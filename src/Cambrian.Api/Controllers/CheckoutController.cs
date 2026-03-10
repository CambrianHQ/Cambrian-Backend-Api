using System.Security.Claims;
using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("")]
public class CheckoutController : BaseController
{
    private readonly ICheckoutService _checkout;

    public CheckoutController(ICheckoutService checkout)
    {
        _checkout = checkout;
    }

    /// <summary>
    /// Create a Stripe Checkout session for a track purchase.
    /// Returns { checkoutUrl } for redirect to Stripe.
    /// </summary>
    [Authorize]
    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout(CheckoutRequest request)
    {
        var session = await _checkout.CreateCheckoutAsync(request, User);
        return OkResponse(new { checkoutUrl = session.CheckoutUrl, status = session.Status });
    }

    /// <summary>
    /// Confirm a completed Stripe checkout session.
    /// Called by the frontend after Stripe redirects back with session_id.
    /// Creates Purchase + LibraryItem + creator wallet credit if payment succeeded.
    /// </summary>
    [Authorize]
    [HttpGet("checkout/session/{sessionId}")]
    public async Task<IActionResult> ConfirmSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return ErrorResponse("sessionId is required.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await _checkout.ConfirmAsync(sessionId, userId);
        return OkResponse(result);
    }
}