using Cambrian.Application.DTOs.Referrals;

namespace Cambrian.Application.Interfaces;

public interface IReferralService
{
    Task<ReferralCodeDto> GetOrCreateReferralCodeAsync(string userId);
    Task<ReferralStatsDto> GetReferralStatsAsync(string userId);
    Task<bool> TrackReferralAsync(string referralCode);
}
