using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class CreatorService : ICreatorService
{
    private readonly ITrackRepository _tracks;
    private readonly IPurchaseRepository _purchases;
    private readonly IPayoutRepository _payouts;

    public CreatorService(ITrackRepository tracks, IPurchaseRepository purchases, IPayoutRepository payouts)
    {
        _tracks = tracks;
        _purchases = purchases;
        _payouts = payouts;
    }

    public async Task<IReadOnlyCollection<TrackResponse>> GetTracksAsync(string userId, int page, int pageSize)
    {
        var tracks = await _tracks.GetByCreatorIdAsync(userId);

        const decimal feeRate = 0.15m;

        return tracks
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t =>
            {
                var nonExPrice = t.NonExclusivePriceCents / 100m;
                var exPrice = t.ExclusivePriceCents / 100m;

                return new TrackResponse
                {
                    Id = t.Id.ToString(),
                    Title = t.Title,
                    Genre = t.Genre ?? "",
                    Price = (decimal)t.Price,
                    NonExclusivePrice = nonExPrice,
                    ExclusivePrice = exPrice,
                    PlatformFeePercent = feeRate,
                    NonExclusivePlatformFee = Math.Round(nonExPrice * feeRate, 2),
                    NonExclusiveCreatorEarnings = Math.Round(nonExPrice * (1 - feeRate), 2),
                    ExclusivePlatformFee = Math.Round(exPrice * feeRate, 2),
                    ExclusiveCreatorEarnings = Math.Round(exPrice * (1 - feeRate), 2),
                    AudioUrl = t.AudioUrl ?? "",
                    CoverArtUrl = t.CoverArtUrl
                };
            })
            .ToList();
    }

    public async Task<object> GetRevenueAsync(string userId)
    {
        var tracks = await _tracks.GetByCreatorIdAsync(userId);
        var trackIds = tracks.Select(t => t.Id).ToHashSet();

        var allPurchases = new List<Domain.Entities.Purchase>();
        foreach (var trackId in trackIds)
        {
            var tp = await _purchases.GetByTrackIdAsync(trackId);
            allPurchases.AddRange(tp.Where(p => p.Status == "completed"));
        }

        const decimal feeRate = 0.15m;
        var grossCents = allPurchases.Sum(p => p.AmountCents);
        var totalGross = grossCents / 100m;
        var totalPlatformFee = Math.Round(totalGross * feeRate, 2);
        var totalEarned = Math.Round(totalGross * (1 - feeRate), 2);

        var payouts = await _payouts.GetByCreatorIdAsync(userId);
        var pendingPayouts = payouts.Where(p => p.Status == "pending").Sum(p => p.AmountCents) / 100m;
        var paidOut = payouts.Where(p => p.Status == "completed").Sum(p => p.AmountCents) / 100m;

        return new
        {
            totalEarned,
            totalGross,
            totalPlatformFee,
            platformFeePercent = feeRate,
            pendingBalance = totalEarned - paidOut - pendingPayouts,
            pendingPayouts,
            paidOut
        };
    }
}
