namespace Cambrian.Application.Interfaces;

/// <summary>
/// Abstraction over the payment provider (Stripe, etc.).
/// Implemented in the Infrastructure layer.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>
    /// Create a hosted checkout session for a one-time payment and return the redirect URL.
    /// </summary>
    Task<string> CreateCheckoutSessionAsync(
        int amountInCents,
        string productName,
        string? clientReferenceId = null,
        string? successUrl = null,
        string? cancelUrl = null);

    /// <summary>
    /// Create a hosted checkout session for a recurring subscription and return the redirect URL.
    /// </summary>
    Task<string> CreateSubscriptionCheckoutAsync(
        int amountInCents,
        string planName,
        string clientReferenceId,
        string successUrl,
        string cancelUrl);

    /// <summary>
    /// Retrieve a checkout session by its ID and return its status + metadata.
    /// </summary>
    Task<CheckoutSessionInfo?> GetCheckoutSessionAsync(string sessionId);
}

/// <summary>
/// Payment-provider-agnostic checkout session details.
/// </summary>
public sealed class CheckoutSessionInfo
{
    public string SessionId { get; init; } = "";
    public string Status { get; init; } = "";
    public string? ClientReferenceId { get; init; }
    public long? AmountTotal { get; init; }
}
