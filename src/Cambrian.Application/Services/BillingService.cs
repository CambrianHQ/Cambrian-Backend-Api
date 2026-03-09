using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public sealed class BillingService : IBillingService
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IPaymentGateway _gateway;

    public BillingService(ISubscriptionRepository subscriptions, IPaymentGateway gateway)
    {
        _subscriptions = subscriptions;
        _gateway = gateway;
    }

    public async Task<CheckoutResponse> CreateCheckoutAsync(string tier, string userId, string frontendUrl)
    {
        var normalizedTier = tier?.ToLowerInvariant() ?? "";
        var (amountCents, planName) = normalizedTier switch
        {
            "paid" => (499, "Paid Listener"),
            "creator" => (999, "Creator"),
            _ => (0, "")
        };

        if (amountCents == 0)
            throw new ArgumentException("Invalid tier. Choose 'paid' or 'creator'.");

        var successUrl = $"{frontendUrl}/checkout/success?session_id={{CHECKOUT_SESSION_ID}}";
        var cancelUrl = $"{frontendUrl}/checkout/cancel";

        var url = await _gateway.CreateSubscriptionCheckoutAsync(
            amountCents,
            planName,
            clientReferenceId: $"{userId}:subscription:{normalizedTier}",
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
}
