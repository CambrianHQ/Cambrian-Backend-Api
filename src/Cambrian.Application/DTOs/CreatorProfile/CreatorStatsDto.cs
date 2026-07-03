namespace Cambrian.Application.DTOs.CreatorProfile;

public class CreatorStatsDto
{
    /// <summary>Total number of completed purchases (downloads/sales) for this creator.</summary>
    public int TotalDownloads { get; set; }

    /// <summary>
    /// Lifetime play count across all of this creator's tracks. Sourced live from
    /// the StreamSessions table. Public-friendly metric (always returned).
    /// </summary>
    public int TotalPlays { get; set; }

    /// <summary>Number of users following this creator. Sourced from CreatorFollows.</summary>
    public int FollowerCount { get; set; }

    // F18: lifetime creator earnings are NOT exposed here. This DTO is serialized on
    // the anonymous storefront/profile routes, so a `totalEarnings` field let any
    // visitor scrape each creator's take-home. Earnings are owner-only and read from
    // the authenticated wallet (PayoutService.GetEarningsAsync).
}
