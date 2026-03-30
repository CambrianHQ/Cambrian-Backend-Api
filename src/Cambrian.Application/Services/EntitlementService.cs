using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

/// <summary>
/// Single source of truth for download entitlement.
/// Delegates to IPurchaseRepository so the "completed purchase = download access" rule
/// lives in exactly one place. Update this class to change the entitlement logic.
/// </summary>
public sealed class EntitlementService : IEntitlementService
{
    private readonly IPurchaseRepository _purchases;

    public EntitlementService(IPurchaseRepository purchases) => _purchases = purchases;

    /// <inheritdoc />
    public Task<bool> CanDownloadAsync(string userId, Guid trackId)
        => _purchases.HasCompletedPurchaseAsync(userId, trackId);
}
