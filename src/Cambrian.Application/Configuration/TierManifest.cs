using Cambrian.Domain.Enums;

namespace Cambrian.Application.Configuration;

/// <summary>
/// Single source of truth for creator tier rules. Every fee rate, upload limit,
/// price, feature flag, and Stripe price binding MUST come from here — no hardcoded
/// values elsewhere.
/// </summary>
public static class TierManifest
{
    public const string ContractVersion = "2.0.0";

    public static readonly TierConfig Free = new()
    {
        Tier = CreatorTier.Free,
        Slug = "free",
        DisplayName = "Free",
        UploadLimit = 10,
        FeeRate = 0.35m,
        PriceCents = 0,
        StripePriceConfigKey = null,
        Features = ["Host up to 10 tracks", "Public profile", "On-chain provenance stamp", "Read-only compliance score"],
        AnalyticsAccess = AnalyticsAccess.Basic,
        FeatureFlags = BuildFeatureFlags(unlimitedAndSuite: false, proOnly: false)
    };

    public static readonly TierConfig Creator = new()
    {
        Tier = CreatorTier.Creator,
        Slug = "creator",
        DisplayName = "Creator",
        UploadLimit = null,  // unlimited
        FeeRate = 0.15m,
        PriceCents = 1500,
        StripePriceConfigKey = "Stripe:Prices:Creator",
        Features =
        [
            "Unlimited tracks", "Full provenance suite", "Unlimited PDF certificates",
            "Commercial-rights verification + Verified Clean badge", "DDEX + C2PA metadata",
            "Royalty + catalog analytics"
        ],
        AnalyticsAccess = AnalyticsAccess.Full,
        FeatureFlags = BuildFeatureFlags(unlimitedAndSuite: true, proOnly: false)
    };

    public static readonly TierConfig Pro = new()
    {
        Tier = CreatorTier.Pro,
        Slug = "pro",
        DisplayName = "Pro / Label",
        UploadLimit = null,  // unlimited
        FeeRate = 0.10m,
        PriceCents = 3900,
        StripePriceConfigKey = "Stripe:Prices:Pro",
        Features =
        [
            "Everything in Creator", "Copyright Office registration assistance",
            "Bulk upload + scheduling", "Sync-pool eligibility", "API access", "Priority support"
        ],
        AnalyticsAccess = AnalyticsAccess.Full,
        FeatureFlags = BuildFeatureFlags(unlimitedAndSuite: true, proOnly: true)
    };

    /// <summary>Resolve config by enum.</summary>
    public static TierConfig For(CreatorTier tier) => tier switch
    {
        CreatorTier.Pro => Pro,
        CreatorTier.Creator => Creator,
        _ => Free
    };

    /// <summary>Resolve config by slug string (case-insensitive). Unknown slugs map to Free.</summary>
    public static TierConfig For(string slug) => (slug ?? "").Trim().ToLowerInvariant() switch
    {
        "pro" => Pro,
        "creator" => Creator,
        _ => Free
    };

    /// <summary>All available tier configurations, cheapest first.</summary>
    public static IReadOnlyList<TierConfig> All => [Free, Creator, Pro];

    /// <summary>
    /// Build the entitlement feature-flag matrix for a tier. Keys are stable, camelCase, and
    /// consumed verbatim by <c>GET /api/me/entitlements</c>. Adding a feature here adds it to
    /// the contract for every tier, so keep keys append-only.
    /// </summary>
    private static IReadOnlyDictionary<string, bool> BuildFeatureFlags(bool unlimitedAndSuite, bool proOnly)
        => new Dictionary<string, bool>
        {
            // All tiers
            ["provenanceStamp"] = true,
            ["complianceScoreRead"] = true,
            // Creator + Pro
            ["unlimitedTracks"] = unlimitedAndSuite,
            ["fullProvenanceSuite"] = unlimitedAndSuite,
            ["pdfCertificates"] = unlimitedAndSuite,
            ["commercialRightsVerification"] = unlimitedAndSuite,
            ["verifiedCleanBadge"] = unlimitedAndSuite,
            ["ddexC2pa"] = unlimitedAndSuite,
            ["routingGuidance"] = unlimitedAndSuite,
            ["catalogAnalytics"] = unlimitedAndSuite,
            // Pro only
            ["copyrightOfficeAssist"] = proOnly,
            ["bulkUpload"] = proOnly,
            ["syncPool"] = proOnly,
            ["apiAccess"] = proOnly,
            ["prioritySupport"] = proOnly,
        };
}

public sealed class TierConfig
{
    public required CreatorTier Tier { get; init; }
    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public required int? UploadLimit { get; init; }
    public required decimal FeeRate { get; init; }
    public required int PriceCents { get; init; }

    /// <summary>
    /// Configuration key holding the Stripe Price ID for this tier's subscription
    /// (e.g. <c>Stripe:Prices:Creator</c>). Null for tiers with no recurring charge (Free).
    /// </summary>
    public string? StripePriceConfigKey { get; init; }

    /// <summary>Human-readable marketing feature list.</summary>
    public required string[] Features { get; init; }

    /// <summary>Machine-readable entitlement flags surfaced by <c>/api/me/entitlements</c>.</summary>
    public required IReadOnlyDictionary<string, bool> FeatureFlags { get; init; }

    public required AnalyticsAccess AnalyticsAccess { get; init; }

    public bool IsUnlimited => UploadLimit is null;
}

public enum AnalyticsAccess
{
    Basic = 0,
    Full = 1
}
