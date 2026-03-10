using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.DTOs.Subscriptions;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

public sealed class BillingService : IBillingService
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IPaymentGateway _gateway;
    private readonly ILogger<BillingService> _logger;
    private readonly string _frontendUrl;

    public BillingService(
        ISubscriptionRepository subscriptions,
        ISubscriptionService subscriptionService,
        IPaymentGateway gateway,
        IConfiguration configuration,
        ILogger<BillingService> logger)
    {
        _subscriptions = subscriptions;
        _subscriptionService = subscriptionService;
        _gateway = gateway;
        _logger = logger;
        _frontendUrl = ResolveFrontendUrl(configuration);
    }

    public async Task<CheckoutResponse> CreateCheckoutAsync(BillingCheckoutRequest request, string userId)
    {
        var tier = request.Tier?.ToLowerInvariant() ?? "";
        var (amountCents, planName) = tier switch
        {
            "paid" => (499, "Paid Listener"),
            "creator" => (999, "Creator"),
            _ => (0, "")
        };

        if (amountCents == 0)
            throw new ArgumentException("Invalid tier. Choose 'paid' or 'creator'.");

        var successUrl = $"{_frontendUrl}/payment?payment_success=true&session_id={{CHECKOUT_SESSION_ID}}";
        var cancelUrl = $"{_frontendUrl}/payment";

        var url = await _gateway.CreateSubscriptionCheckoutAsync(
            amountCents,
            planName,
            clientReferenceId: $"{userId}:subscription:{tier}",
            successUrl,
            cancelUrl);

        return new CheckoutResponse { CheckoutUrl = url };
    }

    public async Task<BillingStatusResponse> GetStatusAsync(string userId)
    {
        var sub = await _subscriptions.GetActiveAsync(userId);
        return new BillingStatusResponse
        {
            Tier = sub?.Plan ?? "free",
            Status = sub?.Status ?? "active",
            ExpiresAt = sub?.ExpiresAt
        };
    }

    public async Task<CheckoutSessionStatusResponse> ConfirmCheckoutAsync(string sessionId, string userId)
    {
        // Retrieve the checkout session from Stripe
        var session = await _gateway.GetCheckoutSessionAsync(sessionId);

        if (session is null)
        {
            _logger.LogWarning("Checkout session {SessionId} not found at Stripe", sessionId);
            return new CheckoutSessionStatusResponse
            {
                SessionId = sessionId,
                Status = "failed"
            };
        }

        if (session.Status != "paid")
        {
            _logger.LogInformation("Checkout session {SessionId} status is {Status}", sessionId, session.Status);
            return new CheckoutSessionStatusResponse
            {
                SessionId = sessionId,
                Status = session.Status
            };
        }

        // Parse clientReferenceId = "userId:subscription:tier"
        var parts = session.ClientReferenceId?.Split(':');
        if (parts is not { Length: 3 } || parts[1] != "subscription")
        {
            _logger.LogWarning("Checkout session {SessionId} has unexpected clientReferenceId: {Ref}",
                sessionId, session.ClientReferenceId);
            return new CheckoutSessionStatusResponse
            {
                SessionId = sessionId,
                Status = "paid"
            };
        }

        var tier = parts[2];

        // Verify the session belongs to this user
        if (parts[0] != userId)
        {
            _logger.LogWarning("Checkout session {SessionId} userId mismatch: session={SessionUser} caller={Caller}",
                sessionId, parts[0], userId);
            return new CheckoutSessionStatusResponse
            {
                SessionId = sessionId,
                Status = "failed"
            };
        }

        // Activate the subscription and update user tier using the existing SubscriptionService logic
        await _subscriptionService.UpdateAsync(new UpdateSubscriptionRequest { Plan = tier }, userId);

        _logger.LogInformation("Checkout confirmed: User={UserId} Tier={Tier} Session={SessionId}",
            userId, tier, sessionId);

        return new CheckoutSessionStatusResponse
        {
            SessionId = sessionId,
            Status = "paid",
            Tier = tier
        };
    }

    private static string ResolveFrontendUrl(IConfiguration configuration)
    {
        var configuredUrl = configuration["App:FrontendUrl"];
        return string.IsNullOrWhiteSpace(configuredUrl)
            ? "http://localhost:5173"
            : configuredUrl.TrimEnd('/');
    }
}
