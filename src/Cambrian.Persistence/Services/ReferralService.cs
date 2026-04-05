using System.Security.Cryptography;
using Cambrian.Application.DTOs.Referrals;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Services;

public class ReferralService : IReferralService
{
    private const string FrontendBaseUrl = "https://cambrianmusic.com";
    private readonly CambrianDbContext _db;

    public ReferralService(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<ReferralCodeDto> GetOrCreateReferralCodeAsync(string userId)
    {
        var existing = await _db.Referrals.FirstOrDefaultAsync(r => r.ReferrerId == userId && r.ReferredUserId == null);

        if (existing is not null)
        {
            return new ReferralCodeDto
            {
                ReferralCode = existing.ReferralCode,
                ReferralLink = $"{FrontendBaseUrl}?ref={existing.ReferralCode}"
            };
        }

        var code = GenerateReferralCode();
        var referral = new Referral
        {
            Id = Guid.NewGuid(),
            ReferrerId = userId,
            ReferralCode = code,
            ReferredAt = DateTime.UtcNow
        };

        _db.Referrals.Add(referral);
        await _db.SaveChangesAsync();

        return new ReferralCodeDto
        {
            ReferralCode = code,
            ReferralLink = $"{FrontendBaseUrl}?ref={code}"
        };
    }

    public async Task<ReferralStatsDto> GetReferralStatsAsync(string userId)
    {
        var referrals = await _db.Referrals
            .Where(r => r.ReferrerId == userId)
            .ToListAsync();

        var code = referrals.FirstOrDefault()?.ReferralCode ?? "";

        return new ReferralStatsDto
        {
            TotalReferrals = referrals.Count(r => r.ReferredUserId != null),
            ConvertedReferrals = referrals.Count(r => r.ConvertedAt.HasValue),
            TotalCommissionEarned = referrals.Sum(r => r.TotalCommissionEarned),
            ReferralLink = $"{FrontendBaseUrl}?ref={code}"
        };
    }

    public async Task<bool> TrackReferralAsync(string referralCode)
    {
        var referral = await _db.Referrals.FirstOrDefaultAsync(r => r.ReferralCode == referralCode);
        return referral is not null;
    }

    private static string GenerateReferralCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(8, chars, static (span, chars) =>
        {
            var bytes = RandomNumberGenerator.GetBytes(8);
            for (int i = 0; i < span.Length; i++)
                span[i] = chars[bytes[i] % chars.Length];
        });
    }
}
