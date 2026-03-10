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

    // ── Stripe Connect ──

    /// <summary>
    /// Create a Stripe Connect Express account for a creator and return the account ID.
    /// </summary>
    Task<string> CreateConnectAccountAsync(string email);

    /// <summary>
    /// Generate an onboarding link for a Connect Express account.
    /// </summary>
    Task<string> CreateAccountOnboardingLinkAsync(string accountId, string returnUrl, string refreshUrl);

    /// <summary>
    /// Check whether a Connect account has completed onboarding and can receive payouts.
    /// </summary>
    Task<ConnectAccountStatus> GetConnectAccountStatusAsync(string accountId);

    /// <summary>
    /// Generate a login link to the Stripe Express dashboard for the connected account.
    /// </summary>
    Task<string> CreateExpressDashboardLinkAsync(string accountId);

    /// <summary>
    /// Transfer funds to a connected account. Returns the transfer ID.
    /// </summary>
    Task<string> CreateTransferAsync(string destinationAccountId, long amountCents, string description);

    /// <summary>
    /// Delete/deauthorize a connected account.
    /// </summary>
    Task DeleteConnectedAccountAsync(string accountId);
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

/// <summary>
/// Status of a connected payment account (Stripe Connect Express).
/// </summary>
public sealed class ConnectAccountStatus
{
    public string AccountId { get; init; } = "";
    /// <summary>"not_connected" | "pending" | "active"</summary>
    public string Status { get; init; } = "not_connected";
    public bool ChargesEnabled { get; init; }
    public bool PayoutsEnabled { get; init; }
}
