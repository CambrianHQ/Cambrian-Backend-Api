namespace Cambrian.Application.DTOs.Entitlements;

/// <summary>
/// Plan-level entitlement matrix returned by <c>GET /api/me/entitlements</c>.
/// This is the server-side source of truth the frontend uses to gate features —
/// distinct from the per-resource <see cref="EntitlementDto"/> grant system.
/// </summary>
public sealed class MeEntitlementsResponse
{
    /// <summary>Tier slug: <c>free</c> | <c>creator</c> | <c>pro</c>.</summary>
    public string Plan { get; set; } = "free";

    /// <summary>Subscription status: <c>active</c> | <c>cancelled</c> | <c>expired</c> | <c>past_due</c>.</summary>
    public string Status { get; set; } = "active";

    public EntitlementLimits Limits { get; set; } = new();

    /// <summary>Feature flag matrix (camelCase keys), resolved from the tier manifest.</summary>
    public IReadOnlyDictionary<string, bool> Features { get; set; } = new Dictionary<string, bool>();
}

public sealed class EntitlementLimits
{
    /// <summary>Maximum hostable tracks; <c>null</c> means unlimited.</summary>
    public int? MaxTracks { get; set; }
}
