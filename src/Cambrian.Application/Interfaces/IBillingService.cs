using Cambrian.Application.DTOs.Billing;

namespace Cambrian.Application.Interfaces;

public interface IBillingService
{
    Task<CheckoutResponse> CreateCheckoutAsync(BillingCheckoutRequest request, string userId, string? userEmail = null);

    Task<BillingStatusResponse> GetStatusAsync(string userId);

    /// <summary>
    /// Retrieve a Stripe checkout session and, if paid, activate the subscription + update user tier.
    /// Returns the session status so the frontend can display appropriate feedback.
    /// </summary>
    Task<CheckoutSessionStatusResponse> ConfirmCheckoutAsync(string sessionId, string userId);
}
