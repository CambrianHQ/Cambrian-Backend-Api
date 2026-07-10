namespace Cambrian.Application.DTOs.Billing;

public class BillingStatusResponse
{
    public string Tier { get; set; } = "free";

    public string Status { get; set; } = "active";

    public DateTime? ExpiresAt { get; set; }

    public DateTime? TrialEndsAt { get; set; }

    /// <summary>Creator tier: "Free" or "Pro".</summary>
    public string CreatorTier { get; set; } = "Free";

    /// <summary>Number of tracks uploaded.</summary>
    public int UploadCount { get; set; }

    /// <summary>Maximum tracks allowed (null = unlimited).</summary>
    public int? UploadLimit { get; set; }

    /// <summary>Platform fee rate for this tier.</summary>
    public decimal PlatformFeePercent { get; set; }
}
