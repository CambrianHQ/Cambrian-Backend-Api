namespace Cambrian.Application.DTOs.Auth;

public class UserProfileResponse
{
    public string UserId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string Tier { get; set; } = string.Empty;

    public bool VerifiedCreator { get; set; }

    /// <summary>Creator tier enum value: "Free" or "Pro".</summary>
    public string CreatorTier { get; set; } = "Free";

    /// <summary>Number of tracks uploaded by this creator.</summary>
    public int UploadCount { get; set; }

    /// <summary>Maximum tracks allowed (null = unlimited).</summary>
    public int? UploadLimit { get; set; }

    /// <summary>Creator subscription status: Active, Inactive, Cancelled.</summary>
    public string SubscriptionStatus { get; set; } = "Inactive";

    /// <summary>When the creator Pro subscription expires.</summary>
    public DateTime? SubscriptionEndDate { get; set; }

    /// <summary>Platform fee rate applied to this creator's sales (0.35 = 35%).</summary>
    public decimal PlatformFeePercent { get; set; }

    /// <summary>Tier contract version for frontend cache invalidation.</summary>
    public string ContractVersion { get; set; } = "1.0.0";
}
