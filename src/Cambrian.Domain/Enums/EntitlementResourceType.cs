namespace Cambrian.Domain.Enums;

/// <summary>
/// The kind of resource an entitlement grants access to. Stored as an int
/// on the Entitlement row — renaming members is safe, but the integer values
/// are load-bearing and must never be reused for a different meaning.
/// </summary>
public enum EntitlementResourceType
{
    Track = 0,
    Collection = 1,
    CreatorSubscription = 2,
    ExclusiveContent = 3,
}
