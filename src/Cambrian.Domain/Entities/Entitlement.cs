using Cambrian.Domain.Enums;

namespace Cambrian.Domain.Entities;

/// <summary>
/// Unified access-control row. One grant per (user, resource, source) — e.g.
/// a purchase creates an Entitlement with SourceType=Purchase pointing at
/// the Purchase.Id; a tip creates one with SourceType=Tip pointing at the
/// Tip.Id; an admin grant points at a support ticket id.
///
/// <para>
/// Revocation is soft: <see cref="RevokedAt"/> is set and audit fields are
/// retained so historical access checks can still be explained. Access
/// checks filter revoked rows out.
/// </para>
/// </summary>
public class Entitlement
{
    public Guid Id { get; set; }

    /// <summary>FK to AspNetUsers.Id.</summary>
    public string UserId { get; set; } = "";

    public EntitlementResourceType ResourceType { get; set; }

    /// <summary>
    /// Resource identifier. Stored as string because different resource types
    /// have different native id types (Track is Guid, Subscription is string,
    /// etc). Callers are responsible for formatting consistently.
    /// </summary>
    public string ResourceId { get; set; } = "";

    public EntitlementAccessLevel AccessLevel { get; set; }

    public EntitlementSourceType SourceType { get; set; }

    /// <summary>
    /// Optional back-reference to the source row (purchase id, subscription
    /// id, tip id, admin ticket id). Purely audit — not used by access checks.
    /// </summary>
    public string? SourceId { get; set; }

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Null = permanent. Access checks ignore the row once this passes.</summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public string? RevokedReason { get; set; }

    // ── Navigation ──
    public ApplicationUser User { get; set; } = null!;
}
