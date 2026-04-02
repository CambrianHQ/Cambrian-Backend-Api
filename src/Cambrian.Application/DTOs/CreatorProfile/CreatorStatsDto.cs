namespace Cambrian.Application.DTOs.CreatorProfile;

public class CreatorStatsDto
{
    /// <summary>Total number of completed purchases (downloads/sales) for this creator.</summary>
    public int TotalDownloads { get; set; }

    /// <summary>
    /// Lifetime net earnings in dollars (post-platform-fee, per-purchase floored).
    /// Sourced from wallet credit transactions. Hidden (returns 0) when ShowEarnings is false.
    /// </summary>
    public decimal TotalEarnings { get; set; }
}
