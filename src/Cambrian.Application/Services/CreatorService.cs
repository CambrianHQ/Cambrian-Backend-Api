using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.Creator;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class CreatorService : ICreatorService
{
    private readonly ITrackRepository _tracks;
    private readonly IPurchaseRepository _purchases;
    private readonly IPayoutRepository _payouts;

    public CreatorService(
        ITrackRepository tracks,
        IPurchaseRepository purchases,
        IPayoutRepository payouts)
    {
        _tracks = tracks;
        _purchases = purchases;
        _payouts = payouts;
    }

    public async Task<IReadOnlyCollection<TrackResponse>> GetTracksAsync(string userId, int page = 1, int pageSize = 50)
    {
        if (page < 1)
        {
            page = 1;
        }

        if (pageSize is < 1 or > 100)
        {
            pageSize = 50;
        }

        var tracks = await _tracks.GetByCreatorIdAsync(userId);

        return tracks
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(track => new TrackResponse
            {
                Id = track.Id.ToString(),
                Title = track.Title,
                Genre = track.Genre ?? "",
                Price = (decimal)track.Price
            })
            .ToList();
    }

    public async Task<CreatorRevenueResponse> GetRevenueAsync(string userId)
    {
        var tracks = await _tracks.GetByCreatorIdAsync(userId);
        var trackIds = tracks.Select(track => track.Id).ToHashSet();
        var allPurchases = new List<Cambrian.Domain.Entities.Purchase>();

        foreach (var trackId in trackIds)
        {
            var purchases = await _purchases.GetByTrackIdAsync(trackId);
            allPurchases.AddRange(purchases.Where(purchase => purchase.Status == "completed"));
        }

        var totalEarned = (decimal)allPurchases.Sum(purchase => purchase.Amount);
        var payouts = await _payouts.GetByCreatorIdAsync(userId);
        var pendingPayouts = (decimal)payouts.Where(payout => payout.Status == "pending").Sum(payout => payout.Amount);
        var paidOut = (decimal)payouts.Where(payout => payout.Status == "completed").Sum(payout => payout.Amount);

        return new CreatorRevenueResponse
        {
            TotalEarned = totalEarned,
            PendingBalance = totalEarned - paidOut - pendingPayouts,
            PendingPayouts = pendingPayouts,
            PaidOut = paidOut
        };
    }
}
