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

    /// <summary>
    /// Lifetime net earnings in dollars (post-platform-fee, per-purchase floored).
    /// Sourced from wallet credit transactions. Hidden (returns 0) when ShowEarnings is false.
    /// </summary>
    public decimal TotalEarnings { get; set; }
}
