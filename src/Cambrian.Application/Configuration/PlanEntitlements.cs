namespace Cambrian.Application.Configuration;

/// <summary>
/// Public, frontend-consumable plan entitlement contract. This is a facade over
/// the authoritative server constants in <see cref="TierManifest"/> and
/// <see cref="CreditPackCatalog"/>; do not copy pricing or limits elsewhere.
/// </summary>
public static class PlanEntitlements
{
    public const int PublicCacheTtlSeconds = 300;
    public const string PdfCertificatesFeatureKey = "pdfCertificates";

    public static PublicPlanEntitlementsResponse PublicContract => new()
    {
        ContractVersion = TierManifest.ContractVersion,
        Plans = TierManifest.All.Select(PlanEntitlementDefinition.From).ToList(),
        ReleaseReady = new ReleaseReadyEntitlementDefinition
        {
            MonthlyCreditsByPlan = TierManifest.All.ToDictionary(
                tier => tier.Slug,
                tier => tier.ReleaseReadyCreditsPerMonth),
            CreditPacks = CreditPackCatalog.Packs
                .Select(pack => new CreditPackEntitlementDefinition
                {
                    Id = pack.Id,
                    Credits = pack.Credits,
                    PriceCents = pack.PriceCents
                })
                .ToList(),
            CreditExpiry = new ReleaseReadyCreditExpiryDefinition
            {
                MonthlyCreditsExpireAtCycleEnd = true,
                PurchasedCreditsExpire = false,
                Summary = "Monthly plan credits reset at the next UTC calendar month and do not roll over. Purchased credit-pack credits never expire."
            }
        }
    };
}

public sealed class PublicPlanEntitlementsResponse
{
    public string ContractVersion { get; init; } = "";
    public IReadOnlyList<PlanEntitlementDefinition> Plans { get; init; } = [];
    public ReleaseReadyEntitlementDefinition ReleaseReady { get; init; } = new();
}

public sealed class PlanEntitlementDefinition
{
    public string Slug { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int? MaxTracks { get; init; }
    public int PriceCents { get; init; }
    public decimal FeeRate { get; init; }
    public int ReleaseReadyCreditsPerMonth { get; init; }
    public IReadOnlyDictionary<string, bool> Features { get; init; } = new Dictionary<string, bool>();

    public static PlanEntitlementDefinition From(TierConfig tier) => new()
    {
        Slug = tier.Slug,
        DisplayName = tier.DisplayName,
        MaxTracks = tier.UploadLimit,
        PriceCents = tier.PriceCents,
        FeeRate = tier.FeeRate,
        ReleaseReadyCreditsPerMonth = tier.ReleaseReadyCreditsPerMonth,
        Features = tier.FeatureFlags
    };
}

public sealed class ReleaseReadyEntitlementDefinition
{
    public IReadOnlyDictionary<string, int> MonthlyCreditsByPlan { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<CreditPackEntitlementDefinition> CreditPacks { get; init; } = [];
    public ReleaseReadyCreditExpiryDefinition CreditExpiry { get; init; } = new();
}

public sealed class CreditPackEntitlementDefinition
{
    public string Id { get; init; } = "";
    public int Credits { get; init; }
    public int PriceCents { get; init; }
}

public sealed class ReleaseReadyCreditExpiryDefinition
{
    public bool MonthlyCreditsExpireAtCycleEnd { get; init; }
    public bool PurchasedCreditsExpire { get; init; }
    public string Summary { get; init; } = "";
}
