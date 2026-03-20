using System.Security.Claims;
using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("")]
public class CheckoutController : BaseController
{
    private readonly ICheckoutService _checkout;
    private readonly ILogger<CheckoutController> _logger;

    public CheckoutController(ICheckoutService checkout, ILogger<CheckoutController> logger)
    {
        _checkout = checkout;
        _logger = logger;
    }

    /// <summary>
    /// Create a Stripe Checkout session for a track purchase.
    /// Returns { checkoutUrl } for redirect to Stripe.
    /// </summary>
    [Authorize]
    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout(CheckoutRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        _logger.LogInformation("EVENT: CheckoutStarted userId:{UserId} trackId:{TrackId} licenseType:{LicenseType}", userId, request.TrackId, request.LicenseType);
        var session = await _checkout.CreateCheckoutAsync(request, User);
        _logger.LogInformation("EVENT: CheckoutSessionCreated userId:{UserId} trackId:{TrackId} checkoutUrl:{CheckoutUrl}", userId, request.TrackId, session.CheckoutUrl);
        return OkResponse(session);
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

        var userId = GetRequiredUserId()!;
        _logger.LogInformation("EVENT: CheckoutConfirmStarted userId:{UserId} sessionId:{SessionId}", userId, sessionId);
        var result = await _checkout.ConfirmAsync(sessionId, userId);
        _logger.LogInformation("EVENT: CheckoutConfirmCompleted userId:{UserId} sessionId:{SessionId} status:{Status}", userId, sessionId, result.Status);
        return OkResponse(result);
    }
}