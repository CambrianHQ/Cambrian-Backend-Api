using Cambrian.Application.DTOs.Entitlements;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Resolves a user's <b>plan-level</b> entitlements (plan, status, limits, feature flags)
/// from their creator tier and active subscription. This is the single source of truth
/// behind <c>GET /api/me/entitlements</c>.
///
/// Intentionally separate from <c>IEntitlementService</c>, which governs per-resource
/// (track/collection) access grants — do not conflate the two.
/// </summary>
public interface IPlanEntitlementService
{
    Task<MeEntitlementsResponse> ResolveAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Throw <see cref="Exceptions.UpgradeRequiredException"/> (→ HTTP 402 <c>UPGRADE_REQUIRED</c>)
    /// when the user's plan does not enable <paramref name="featureKey"/>. The single server-side
    /// reuse point for plan-feature gating — keys are the camelCase flags from the tier manifest
    /// (e.g. <c>fullProvenanceSuite</c>, <c>provenanceStamp</c>, <c>complianceScoreRead</c>).
    /// </summary>
    Task RequireFeatureAsync(string userId, string featureKey, CancellationToken ct = default);
}
