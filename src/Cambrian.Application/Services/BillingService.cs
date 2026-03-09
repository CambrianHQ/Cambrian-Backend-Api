using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Cambrian.Application.Services;

public sealed class BillingService : IBillingService
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IPaymentGateway _gateway;
    private readonly string _frontendUrl;

    public BillingService(
        ISubscriptionRepository subscriptions,
        IPaymentGateway gateway,
        IConfiguration configuration)
    {
        _subscriptions = subscriptions;
        _gateway = gateway;
        _frontendUrl = configuration["App:FrontendUrl"] ?? "http://localhost:5173";
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

        var successUrl = $"{_frontendUrl}/checkout/success?session_id={{CHECKOUT_SESSION_ID}}";
        var cancelUrl = $"{_frontendUrl}/checkout/cancel";

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
}
