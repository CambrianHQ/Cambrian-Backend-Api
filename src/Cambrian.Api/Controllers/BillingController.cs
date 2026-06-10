using System.Security.Claims;
using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("billing")]
[Authorize]
public class BillingController : BaseController
{
    private readonly IBillingService _billing;
    private readonly ILogger<BillingController> _logger;
    private readonly IConfiguration _config;

    public BillingController(IBillingService billing, ILogger<BillingController> logger, IConfiguration config)
    {
        _billing = billing;
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Launch kill switch (residue #6). Checkout is on by default; set
    /// CHECKOUT_ENABLED=false (or Checkout:Enabled=false) to make every
    /// checkout-session endpoint return 503 with a friendly code — used to stop
    /// new charges during an incident without a redeploy.
    /// </summary>
    private bool CheckoutEnabled =>
        _config.GetValue<bool?>("Checkout:Enabled")
        ?? _config.GetValue<bool?>("CHECKOUT_ENABLED")
        ?? true;

    private IActionResult CheckoutDisabledResponse()
    {
        _logger.LogWarning("EVENT: BillingCheckoutBlocked reason:checkout_disabled");
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            success = false,
            data = (object?)null,
            message = "Checkout is temporarily unavailable. Please try again shortly.",
            error = "checkout_disabled",
        });
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout(BillingCheckoutRequest request)
    {
        if (!CheckoutEnabled) return CheckoutDisabledResponse();

        var userId = GetRequiredUserId()!;
        var userEmail = User.FindFirstValue(ClaimTypes.Email)
                     ?? User.FindFirstValue("email");

        if (User.IsInRole("Admin"))
            return ErrorResponse("Admin accounts cannot purchase subscriptions.");

        _logger.LogInformation("EVENT: BillingCheckoutStarted userId:{UserId} tier:{Tier}", userId, request.Tier);
        Cambrian.Application.Observability.CambrianMetrics.CheckoutStarted.Add(1);

        try
        {
            var result = await _billing.CreateCheckoutAsync(request, userId, userEmail);
            _logger.LogInformation("EVENT: BillingCheckoutCreated userId:{UserId} tier:{Tier}", userId, request.Tier);
            return OkResponse(new { url = result.CheckoutUrl, checkoutUrl = result.CheckoutUrl });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "EVENT: BillingCheckoutFailed userId:{UserId} tier:{Tier}", userId, request.Tier);
            Cambrian.Application.Observability.CambrianMetrics.CheckoutFailed.Add(1);
            return ErrorResponse(ex.Message);
        }
    }

    [HttpPost("checkout-session")]
    public async Task<IActionResult> CheckoutSession(BillingCheckoutRequest request)
    {
        return await Checkout(request);
    }

    // ───── Spec-canonical /api routes (frontend contract) ─────

    /// <summary>POST /api/billing/checkout { tier } → { checkoutUrl }.</summary>
    [HttpPost("/api/billing/checkout")]
    public async Task<IActionResult> ApiCheckout(BillingCheckoutRequest request)
    {
        if (!CheckoutEnabled) return CheckoutDisabledResponse();

        var userId = GetRequiredUserId()!;
        var userEmail = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email");

        if (User.IsInRole("Admin"))
            return ErrorResponse("Admin accounts cannot purchase subscriptions.");

        _logger.LogInformation("EVENT: BillingCheckoutStarted userId:{UserId} tier:{Tier}", userId, request.Tier);
        Cambrian.Application.Observability.CambrianMetrics.CheckoutStarted.Add(1);

        try
        {
            var result = await _billing.CreateCheckoutAsync(request, userId, userEmail);
            _logger.LogInformation("EVENT: BillingCheckoutCreated userId:{UserId} tier:{Tier}", userId, request.Tier);
            return OkResponse(new { checkoutUrl = result.CheckoutUrl });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "EVENT: BillingCheckoutFailed userId:{UserId} tier:{Tier}", userId, request.Tier);
            Cambrian.Application.Observability.CambrianMetrics.CheckoutFailed.Add(1);
            return ErrorResponse(ex.Message);
        }
    }

    /// <summary>POST /api/billing/portal → { portalUrl } (Stripe Customer Portal).</summary>
    [HttpPost("/api/billing/portal")]
    public async Task<IActionResult> ApiPortal()
    {
        var userId = GetRequiredUserId()!;
        var userEmail = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email");

        try
        {
            var result = await _billing.CreatePortalAsync(userId, userEmail);
            _logger.LogInformation("EVENT: BillingPortalRequested userId:{UserId}", userId);
            return OkResponse(new { portalUrl = result.PortalUrl });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "EVENT: BillingPortalFailed userId:{UserId}", userId);
            return ErrorResponse(ex.Message);
        }
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var userId = GetRequiredUserId()!;
        var status = await _billing.GetStatusAsync(userId);
        _logger.LogInformation("EVENT: BillingStatusRetrieved userId:{UserId} tier:{Tier}", userId, status.Tier);
        return OkResponse(status);
    }

    [HttpGet("checkout-session/{sessionId}")]
    public async Task<IActionResult> GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return ErrorResponse("sessionId is required.");

        var userId = GetRequiredUserId()!;
        _logger.LogInformation("EVENT: BillingSessionConfirmStarted userId:{UserId} sessionId:{SessionId}", userId, sessionId);
        var result = await _billing.ConfirmCheckoutAsync(sessionId, userId);
        _logger.LogInformation("EVENT: BillingSessionConfirmCompleted userId:{UserId} sessionId:{SessionId} status:{Status}", userId, sessionId, result.Status);
        return OkResponse(result);
    }
}
