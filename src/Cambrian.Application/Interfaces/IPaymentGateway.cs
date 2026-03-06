namespace Cambrian.Application.Interfaces;

/// <summary>
/// Abstraction over the payment provider (Stripe, etc.).
/// Implemented in the Infrastructure layer.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>
    /// Create a hosted checkout session and return the redirect URL.
    /// </summary>
    Task<string> CreateCheckoutSessionAsync(
        int amountInCents,
        string productName,
        string? clientReferenceId = null,
        string? successUrl = null,
        string? cancelUrl = null);
}
