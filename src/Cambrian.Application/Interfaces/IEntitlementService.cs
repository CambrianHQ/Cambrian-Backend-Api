using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Single source of truth for access control across the platform.
///
/// <para>
/// <b>Legacy download path:</b> <see cref="CanDownloadAsync"/> preserves the
/// original contract — "a completed purchase grants download access" — and
/// remains backed by <c>IPurchaseRepository</c>. Existing download endpoints
/// call this and must not be migrated onto the new table until a backfill
/// runs (protected data — touches paid customers).
/// </para>
///
/// <para>
/// <b>Unified entitlement path:</b> <see cref="HasAccessAsync"/> and
/// <see cref="GrantAsync"/> operate on the <see cref="Entitlement"/> table.
/// New features (tips, subscriptions, promotions, exclusive content) should
/// grant rows here and check access here — never by querying purchase /
/// subscription tables inline.
/// </para>
/// </summary>
public interface IEntitlementService
{
    /// <summary>
    /// Legacy: returns true when the user has at least one completed purchase
    /// for this track. Semantics are frozen — do not change without a data
    /// migration plan.
    /// </summary>
    Task<bool> CanDownloadAsync(string userId, Guid trackId);

    /// <summary>
    /// Record that a user has been granted access to a resource at a given
    /// access level, sourced from a specific purchase/subscription/tip/etc.
    /// Returns the persisted row.
    /// </summary>
    Task<Entitlement> GrantAsync(
        string userId,
        EntitlementResourceType resourceType,
        string resourceId,
        EntitlementAccessLevel accessLevel,
        EntitlementSourceType sourceType,
        string? sourceId = null,
        DateTime? expiresAt = null,
        CancellationToken ct = default);

    /// <summary>
    /// Soft-revoke an entitlement. The row is preserved for audit; subsequent
    /// <see cref="HasAccessAsync"/> calls ignore revoked rows. Returns false
    /// when the id does not exist or the row is already revoked.
    /// </summary>
    Task<bool> RevokeAsync(Guid entitlementId, string reason, CancellationToken ct = default);

    /// <summary>
    /// True when the user holds a non-revoked, non-expired entitlement for
    /// the resource at <paramref name="requiredLevel"/> or higher. Access
    /// levels are ranked — <c>License</c> satisfies <c>Download</c> satisfies
    /// <c>Stream</c>.
    /// </summary>
    Task<bool> HasAccessAsync(
        string userId,
        EntitlementResourceType resourceType,
        string resourceId,
        EntitlementAccessLevel requiredLevel,
        CancellationToken ct = default);

    /// <summary>
    /// List entitlements belonging to a user. By default revoked rows are
    /// hidden; pass <paramref name="includeRevoked"/> = true for admin audit
    /// views.
    /// </summary>
    Task<List<Entitlement>> GetForUserAsync(
        string userId,
        EntitlementResourceType? resourceType = null,
        bool includeRevoked = false,
        CancellationToken ct = default);
}
