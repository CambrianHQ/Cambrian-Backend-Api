namespace Cambrian.Domain.Enums;

/// <summary>
/// How an entitlement was obtained. Used for audit and for feature-specific
/// revocation rules (e.g. a subscription-sourced entitlement is auto-revoked
/// when the underlying subscription lapses).
/// </summary>
public enum EntitlementSourceType
{
    Purchase = 0,
    Subscription = 1,
    Tip = 2,
    Promotion = 3,
    Admin = 4,
}
