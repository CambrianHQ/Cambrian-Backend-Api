using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("billing")]
[Authorize]
public class BillingController : BaseController
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IPaymentGateway _payments;
    private readonly IConfiguration _config;

    public BillingController(ISubscriptionRepository subscriptions, IPaymentGateway payments, IConfiguration config)
    {
        _subscriptions = subscriptions;
        _payments = payments;
        _config = config;
    }

    /// <summary>
    /// Create a Stripe Checkout session for a tier upgrade.
    /// </summary>
    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout([FromBody] BillingCheckoutRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        var tier = request.Tier.ToLowerInvariant();
        var priceInCents = tier switch
        {
            "paid" => 999,
            "creator" => 1999,
            _ => 0
        };

        if (priceInCents == 0)
            return ErrorResponse("Invalid tier. Choose 'paid' or 'creator'.");

        var frontendBase = _config["App:FrontendUrl"] ?? "http://localhost:5173";

        // If Stripe is configured, create a real checkout session
        var stripeKey = _config["Stripe:SecretKey"];
        if (!string.IsNullOrWhiteSpace(stripeKey))
        {
            var successUrl = $"{frontendBase}/payment?payment_success=true&tier={tier}&session_id={{CHECKOUT_SESSION_ID}}";
            var cancelUrl = $"{frontendBase}/payment?cancelled=true";

            var checkoutUrl = await _payments.CreateSubscriptionCheckoutAsync(
                priceInCents,
                tier,
                userId,
                successUrl,
                cancelUrl);

            return Ok(new { url = checkoutUrl });
        }

        // Dev fallback: apply subscription directly (no Stripe keys configured)
        var existing = await _subscriptions.GetActiveAsync(userId);
        if (existing is not null)
            await _subscriptions.CancelAsync(existing.Id);

        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Plan = tier,
            Status = "active",
            StartedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMonths(1)
        };
        await _subscriptions.CreateAsync(subscription);

        var devSuccessUrl = $"{frontendBase}/payment?payment_success=true&tier={tier}&session_id={subscription.Id}";
        return Ok(new { url = devSuccessUrl });
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var sub = await _subscriptions.GetActiveAsync(userId);

        return Ok(new
        {
            tier = sub?.Plan ?? "free",
            tierUpdatedAt = (sub?.StartedAt ?? DateTime.UtcNow).ToString("o"),
            tierExpiresAt = sub?.ExpiresAt?.ToString("o"),
            status = sub?.Status ?? "active"
        });
    }

    [HttpGet("checkout-session/{sessionId}")]
    public async Task<IActionResult> GetSession(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out _))
            return ErrorResponse("Invalid session ID.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var sub = await _subscriptions.GetActiveAsync(userId);

        return Ok(new
        {
            status = sub is not null ? "paid" : "pending",
            tier = sub?.Plan ?? "free",
            paid = sub is not null
        });
    }
}

public class BillingCheckoutRequest
{
    public string Tier { get; set; } = "paid";
}
