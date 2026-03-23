namespace Cambrian.Application.Interfaces;

/// <summary>
/// Manages Stripe Connect onboarding, status, dashboard, and disconnection for creators.
/// </summary>
public interface ICreatorConnectService
{
    /// <summary>
    /// Create or retrieve a Stripe Connect Express account for the creator
    /// and return an onboarding URL.
    /// </summary>
    Task<CreatorConnectResult> StartOnboardingAsync(string userId);

    /// <summary>
    /// Check whether the creator's Stripe account is connected and ready for payouts.
    /// </summary>
    Task<CreatorConnectStatusResponse> GetStatusAsync(string userId);

    /// <summary>
    /// Generate a link to the Stripe Express dashboard for the creator.
    /// </summary>
    Task<string?> GetDashboardLinkAsync(string userId);

    /// <summary>
    /// Disconnect (delete) the creator's Stripe Connect account.
    /// </summary>
    Task DisconnectAsync(string userId);
}

public sealed class CreatorConnectResult
{
    public string? ConnectUrl { get; init; }
    public string Status { get; init; } = "pending";
}

public sealed class CreatorConnectStatusResponse
{
    public bool Connected { get; init; }
    // SECURITY: AccountId (Stripe acct_xxx) removed — internal identifier should not be exposed to client
    /// <summary>"not_connected" | "pending" | "active"</summary>
    public string Status { get; init; } = "not_connected";
}
