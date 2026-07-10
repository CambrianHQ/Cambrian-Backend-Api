using Cambrian.Application.Configuration;
using Microsoft.Extensions.Configuration;

namespace Cambrian.Application.Pricing;

/// <summary>
/// Single import surface for every monetized product's price, monthly grant, and
/// Stripe binding. This is a FACADE: it delegates to the existing authoritative
/// sources (<see cref="TierManifest"/>, <see cref="CreditPackCatalog"/>, and the
/// Authorship Record price) rather than copying their numbers, so the contract can
/// never drift from what checkout/webhook code actually charges.
///
/// <para>Stable product keys (<see cref="Keys"/>) are shared by frontend, backend,
/// and the release-gate tests. Pricing is server-authoritative — clients never
/// dictate amounts.</para>
/// </summary>
public static class PricingContract
{
    /// <summary>Stable product identifiers used across frontend/backend/tests.</summary>
    public static class Keys
    {
        public const string CreatorSubscription = "sub.creator";
        public const string ProSubscription = "sub.pro";
        public const string CreditSingle = "credit.single";
        public const string CreditTriple = "credit.triple";
        public const string CreditTen = "credit.ten";
        public const string AuthorshipRecord = "authorship.record";
    }

    // ── Subscriptions (delegate to TierManifest) ──
    public static int CreatorSubscriptionCents => TierManifest.Creator.PriceCents; // 599
    public static int ProSubscriptionCents => TierManifest.Pro.PriceCents;         // 1299

    // ── Release Ready monthly credit grants (delegate to TierManifest) ──
    public static int CreatorMonthlyReleaseReadyCredits => TierManifest.Creator.ReleaseReadyCreditsPerMonth; // 3
    public static int ProMonthlyReleaseReadyCredits => TierManifest.Pro.ReleaseReadyCreditsPerMonth;         // 10

    // ── One-time credit packs (delegate to CreditPackCatalog) ──
    public static CreditPack SingleCreditPack => CreditPackCatalog.Find("single")!; // 1 credit / 900c
    public static CreditPack TripleCreditPack => CreditPackCatalog.Find("triple")!; // 3 credits / 2400c
    public static CreditPack TenCreditPack => CreditPackCatalog.Find("ten")!;        // 10 credits / 6900c

    /// <summary>Resolve a credit pack by its stable product key (e.g. "credit.triple").</summary>
    public static CreditPack? CreditPackForKey(string productKey) => productKey switch
    {
        Keys.CreditSingle => SingleCreditPack,
        Keys.CreditTriple => TripleCreditPack,
        Keys.CreditTen => TenCreditPack,
        _ => null
    };

    // ── Authorship Record (mirrors AuthorshipRecordService.DefaultPriceCents) ──
    /// <summary>Launch default ($10). Config key <c>AuthorshipRecord:PriceCents</c> overrides it.</summary>
    public const int AuthorshipRecordDefaultCents = 1000;

    /// <summary>
    /// Configured Authorship Record price, honoring the <c>AuthorshipRecord:PriceCents</c>
    /// override. Mirrors <c>AuthorshipRecordService</c> exactly so the two never diverge.
    /// </summary>
    public static int ResolveAuthorshipRecordCents(IConfiguration config)
        => config.GetValue<int?>("AuthorshipRecord:PriceCents") ?? AuthorshipRecordDefaultCents;

    /// <summary>
    /// Resolve the Stripe Price ID for a subscription product key, reading the same
    /// config keys <c>BillingService</c> uses (<c>Stripe:Prices:Creator</c> / <c>:Pro</c>).
    /// Returns null when the key is unknown or unconfigured.
    /// </summary>
    public static string? ResolveSubscriptionPriceId(string productKey, IConfiguration config)
    {
        var tier = productKey switch
        {
            Keys.CreatorSubscription => TierManifest.Creator,
            Keys.ProSubscription => TierManifest.Pro,
            _ => null
        };

        return tier?.StripePriceConfigKey is { } key ? config[key] : null;
    }
}
