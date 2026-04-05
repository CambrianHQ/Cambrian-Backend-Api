namespace Cambrian.Application.DTOs.Referrals;

public class ReferralCodeDto
{
    public string ReferralCode { get; set; } = null!;
    public string ReferralLink { get; set; } = null!;
}

public class ReferralStatsDto
{
    public int TotalReferrals { get; set; }
    public int ConvertedReferrals { get; set; }
    public decimal TotalCommissionEarned { get; set; }
    public string ReferralLink { get; set; } = null!;
}

public class TrackReferralRequest
{
    public string ReferralCode { get; set; } = null!;
}
