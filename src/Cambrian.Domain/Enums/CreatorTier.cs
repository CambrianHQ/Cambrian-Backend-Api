namespace Cambrian.Domain.Enums;

/// <summary>
/// Creator subscription tier. Determines upload limits, platform fees, and feature access.
/// </summary>
public enum CreatorTier
{
    /// <summary>Free tier: 10 track limit, 35% platform fee, basic analytics.</summary>
    Free = 0,

    /// <summary>Pro tier: unlimited uploads, 15% platform fee, full analytics, featured placement.</summary>
    Pro = 1
}
