namespace Cambrian.Application.DTOs.Public;

/// <summary>
/// Aggregate, public platform statistics. Every value is a real count computed from
/// the database. No revenue or other financial figures are included.
/// </summary>
public sealed class PublicPlatformStatsDto : PublicSeoResource
{
    /// <summary>Number of public tracks.</summary>
    public int TrackCount { get; set; }

    /// <summary>Number of creators with public content.</summary>
    public int CreatorCount { get; set; }

    /// <summary>Number of distinct genres present in the public catalogue.</summary>
    public int GenreCount { get; set; }

    /// <summary>Lifetime qualified plays across eligible public tracks.</summary>
    public long TotalPlays { get; set; }

    /// <summary>Number of issued (paid, signed) authorship records.</summary>
    public int AuthorshipRecordCount { get; set; }
}

/// <summary>
/// Public pricing for creator plans, sourced from the canonical tier manifest.
/// This is marketing/plan information only — no Stripe price IDs are exposed.
/// </summary>
public sealed class PublicPricingDto : PublicSeoResource
{
    public string Currency { get; set; } = "USD";

    /// <summary>Plan tiers, cheapest first.</summary>
    public List<PublicPricingTierDto> Tiers { get; set; } = new();
}

/// <summary>A single public pricing tier.</summary>
public sealed class PublicPricingTierDto
{
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Monthly price in whole currency units.</summary>
    public decimal PriceMonthly { get; set; }

    /// <summary>Monthly price in cents — canonical integer money value.</summary>
    public int PriceCentsMonthly { get; set; }

    public string BillingPeriod { get; set; } = "month";

    /// <summary>Whether the tier is free.</summary>
    public bool IsFree { get; set; }

    /// <summary>Upload limit (null = unlimited).</summary>
    public int? UploadLimit { get; set; }

    /// <summary>Platform fee applied to each sale, as a percentage (e.g. 15 = 15%).</summary>
    public decimal PlatformFeePercent { get; set; }

    /// <summary>Human-readable marketing feature list.</summary>
    public List<string> Features { get; set; } = new();
}
