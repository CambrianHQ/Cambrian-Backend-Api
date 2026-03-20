using Cambrian.Domain.Enums;

namespace Cambrian.Application.Configuration;

/// <summary>
/// Single source of truth for creator tier rules. Every fee rate, upload limit,
/// price, and feature flag MUST come from here — no hardcoded values elsewhere.
/// </summary>
public static class TierManifest
{
    public const string ContractVersion = "1.0.0";

    public static readonly TierConfig Free = new()
    {
        Tier = CreatorTier.Free,
        Slug = "free",
        DisplayName = "Free Creator",
        UploadLimit = 10,
        FeeRate = 0.35m,
        PriceCents = 0,
        Features = ["Upload up to 10 tracks", "Sell licenses", "Basic analytics"],
        AnalyticsAccess = AnalyticsAccess.Basic
    };

    public static readonly TierConfig Pro = new()
    {
        Tier = CreatorTier.Pro,
        Slug = "pro",
        DisplayName = "Pro Creator",
        UploadLimit = null,  // unlimited
        FeeRate = 0.15m,
        PriceCents = 999,
        Features = ["Unlimited uploads", "Sell licenses", "Full analytics", "Featured placement", "Priority support"],
        AnalyticsAccess = AnalyticsAccess.Full
    };

    /// <summary>Resolve config by enum.</summary>
    public static TierConfig For(CreatorTier tier) => tier switch
    {
        CreatorTier.Pro => Pro,
        _ => Free
    };

    /// <summary>Resolve config by slug string (case-insensitive). Only "pro" maps to Pro tier.</summary>
    public static TierConfig For(string slug) =>
        slug.Equals("pro", StringComparison.OrdinalIgnoreCase)
            ? Pro
            : Free;

    /// <summary>All available tier configurations.</summary>
    public static IReadOnlyList<TierConfig> All => [Free, Pro];
}

public sealed class TierConfig
{
    public required CreatorTier Tier { get; init; }
    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public required int? UploadLimit { get; init; }
    public required decimal FeeRate { get; init; }
    public required int PriceCents { get; init; }
    public required string[] Features { get; init; }
    public required AnalyticsAccess AnalyticsAccess { get; init; }

    public bool IsUnlimited => UploadLimit is null;
}

public enum AnalyticsAccess
{
    Basic = 0,
    Full = 1
}
