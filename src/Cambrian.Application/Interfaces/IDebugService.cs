namespace Cambrian.Application.Interfaces;

/// <summary>
/// Admin-only diagnostic service for debugging user state, webhook history, and library consistency.
/// </summary>
public interface IDebugService
{
    /// <summary>Returns full diagnostic state for a user: profile, tier, subscription, purchases, library items, and recent webhooks.</summary>
    Task<object?> GetUserStateAsync(string userId);

    /// <summary>Returns recent Stripe webhook events with optional filtering.</summary>
    Task<object> GetRecentWebhooksAsync(int limit = 25, string? eventType = null);

    /// <summary>Finds completed purchases that have no matching library item.</summary>
    Task<object> RunConsistencyCheckAsync();
}
