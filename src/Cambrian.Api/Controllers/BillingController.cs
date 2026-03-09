using System.Security.Claims;
using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Cambrian.Api.Controllers;

[Route("billing")]
[Authorize]
public class BillingController : BaseController
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IPaymentGateway _gateway;
    private readonly string _frontendUrl;

    public BillingController(
        ISubscriptionRepository subscriptions,
        IPaymentGateway gateway,
        IConfiguration configuration)
    {
        _subscriptions = subscriptions;
        _gateway = gateway;
        _frontendUrl = ResolveFrontendUrl(configuration);
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout(BillingCheckoutRequest request)
    {
        var tier = request.Tier?.ToLowerInvariant() ?? "";

        var (amountCents, planName) = tier switch
        {
            "paid" => (499, "Paid Listener"),
            "creator" => (999, "Creator"),
            _ => (0, "")
        };

        if (amountCents == 0)
            return ErrorResponse("Invalid tier. Choose 'paid' or 'creator'.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var successUrl = $"{_frontendUrl}/hub?payment_success=true&session_id={{CHECKOUT_SESSION_ID}}";
        var cancelUrl = $"{_frontendUrl}/payment?plan={tier}";

        var url = await _gateway.CreateSubscriptionCheckoutAsync(
            amountCents,
            planName,
            clientReferenceId: $"{userId}:subscription:{tier}",
            successUrl,
            cancelUrl);

        return OkResponse(new { url });
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
        var sub = await _subscriptions.GetActiveAsync(userId);

        return OkResponse(new
        {
            tier = sub?.Plan ?? "free",
            status = sub?.Status ?? "active"
        });
    }

    [HttpGet("checkout-session/{sessionId}")]
    public IActionResult GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return ErrorResponse("sessionId is required.");
        return OkResponse(new { sessionId });
    }

    private static string ResolveFrontendUrl(IConfiguration configuration)
    {
        var configuredUrl = configuration["App:FrontendUrl"];
        return string.IsNullOrWhiteSpace(configuredUrl)
            ? "http://localhost:5173"
            : configuredUrl.TrimEnd('/');
    }
}
