namespace Cambrian.Application.DTOs.Public;

/// <summary>
/// Public, SEO/AI-safe creator profile. Built only from public storefront data:
/// display name, bio, niche, public images, public social links, real engagement
/// stats, and a sample of public tracks. Excludes email, Stripe account, wallet
/// balance, earnings, and any private/admin fields.
/// </summary>
public sealed class PublicCreatorDto : PublicSeoResource
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Canonical storefront slug (used in the public URL).</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Canonical routing username, when present.</summary>
    public string? Username { get; set; }

    public string DisplayName { get; set; } = string.Empty;
    public string? Bio { get; set; }
    public string? Niche { get; set; }

    /// <summary>Public social links (platform + url only).</summary>
    public List<PublicSocialLinkDto> SocialLinks { get; set; } = new();

    /// <summary>Real, non-financial engagement stats.</summary>
    public PublicCreatorStatsDto Stats { get; set; } = new();

    /// <summary>Total number of public tracks by this creator.</summary>
    public int TrackCount { get; set; }

    /// <summary>A sample of the creator's most recent public tracks.</summary>
    public List<PublicTrackDto> RecentTracks { get; set; } = new();
}

/// <summary>
/// Lightweight public creator entry used in search results and featured lists.
/// </summary>
public sealed class PublicCreatorSummaryDto : PublicSeoResource
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Niche { get; set; }

    /// <summary>Total number of public tracks by this creator.</summary>
    public int TrackCount { get; set; }
}

/// <summary>
/// Real, non-financial engagement stats for a creator. Qualified plays come from the
/// transactionally maintained projection. Earnings/payout/revenue figures are absent.
/// </summary>
public sealed class PublicCreatorStatsDto
{
    /// <summary>Lifetime qualified plays across all of the creator's tracks.</summary>
    public long Plays { get; set; }

    /// <summary>Number of followers (CreatorFollows).</summary>
    public int Followers { get; set; }

    /// <summary>Completed sales across all of the creator's tracks.</summary>
    public int Sales { get; set; }

    /// <summary>Number of public tracks.</summary>
    public int TrackCount { get; set; }
}

/// <summary>Public social link: platform name and URL only.</summary>
public sealed class PublicSocialLinkDto
{
    public string Platform { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
