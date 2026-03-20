using System.Security.Claims;
using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("billing")]
[Authorize]
public class BillingController : BaseController
{
    private readonly IBillingService _billing;
    private readonly ILogger<BillingController> _logger;

    public BillingController(IBillingService billing, ILogger<BillingController> logger)
    {
        _billing = billing;
        _logger = logger;
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout(BillingCheckoutRequest request)
    {
        var userId = GetRequiredUserId()!;
        var userEmail = User.FindFirstValue(ClaimTypes.Email)
                     ?? User.FindFirstValue("email");

        if (User.IsInRole("Admin"))
            return ErrorResponse("Admin accounts cannot purchase subscriptions.");

        _logger.LogInformation("EVENT: BillingCheckoutStarted userId:{UserId} tier:{Tier}", userId, request.Tier);

        try
        {
            var result = await _billing.CreateCheckoutAsync(request, userId, userEmail);
            _logger.LogInformation("EVENT: BillingCheckoutCreated userId:{UserId} tier:{Tier}", userId, request.Tier);
            return OkResponse(new { url = result.CheckoutUrl });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "EVENT: BillingCheckoutFailed userId:{UserId} tier:{Tier}", userId, request.Tier);
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
