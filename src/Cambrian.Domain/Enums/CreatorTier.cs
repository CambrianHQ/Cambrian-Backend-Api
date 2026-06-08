namespace Cambrian.Domain.Enums;

/// <summary>
/// Creator subscription tier. Determines upload limits, platform fees, and feature access.
/// Persisted as <c>int</c>; values MUST NOT be renumbered (existing Pro rows are stored as 1).
/// New tiers are appended with new integer values.
/// </summary>
public enum CreatorTier
{
    /// <summary>Free tier ($0): up to 10 tracks, 35% platform fee, basic analytics, provenance stamp.</summary>
    Free = 0,

    /// <summary>Pro/Label tier ($39/mo): unlimited uploads, 10% platform fee, full suite + API + sync pool.</summary>
    Pro = 1,

    /// <summary>Creator tier ($15/mo): unlimited uploads, 15% platform fee, full provenance suite + analytics.</summary>
    Creator = 2
}
