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
        ReleaseReadyCreditsPerMonth = 0,
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
        // Marketing copy published via /tiers/config — list only what works today.
        // Roadmap items (PDF certificates, DDEX/C2PA export, Verified Clean badge) are
        // surfaced as "coming soon" on the frontend, not advertised as included here.
        Features =
        [
            "Unlimited tracks", "3 Release Ready masters per month",
            "Full provenance suite (stamp + compliance score)",
            "Commercial-rights attestation", "Creator analytics"
        ],
        AnalyticsAccess = AnalyticsAccess.Full,
        ReleaseReadyCreditsPerMonth = 3,
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
        // Roadmap items (Copyright Office assistance, bulk upload + scheduling, sync-pool)
        // are surfaced as "coming soon" on the frontend, not advertised as included here.
        Features =
        [
            "Everything in Creator", "10 Release Ready masters per month",
            "API access", "Priority support"
        ],
        AnalyticsAccess = AnalyticsAccess.Full,
        ReleaseReadyCreditsPerMonth = 10,
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
    /// <para>
    /// ⚠️ Several of these flags are ROADMAP markers, not enforced/implemented features. As of
    /// 2026-06 only <c>complianceScoreRead</c>, <c>provenanceStamp</c>, <c>fullProvenanceSuite</c>,
    /// <c>catalogAnalytics</c>, and <c>apiAccess</c> back a working capability; <c>unlimitedTracks</c>
    /// is enforced via UploadLimit. The rest — <c>pdfCertificates</c>, <c>commercialRightsVerification</c>,
    /// <c>verifiedCleanBadge</c>, <c>ddexC2pa</c>, <c>routingGuidance</c>, <c>copyrightOfficeAssist</c>,
    /// <c>bulkUpload</c>, <c>syncPool</c> — have no implementation and are surfaced as "coming soon".
    /// Returning a flag here does NOT grant a real feature; don't treat these as live entitlements.
    /// </para>
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
            // Not implemented on any tier yet — surfaced so the frontend hides the paid CTA.
            ["commissionsEnabled"] = false,
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

    /// <summary>
    /// Release Ready mastering credits granted per calendar month (0 = none).
    /// Per-tier config constant — differentiated amounts (e.g. Creator 3 / Pro 10)
    /// are a one-line change here once canonical pricing is confirmed.
    /// </summary>
    public int ReleaseReadyCreditsPerMonth { get; init; }

    public bool IsUnlimited => UploadLimit is null;
}

public enum AnalyticsAccess
{
    Basic = 0,
    Full = 1
}
