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

        return tracks
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TrackResponse
            {
                Id = t.Id.ToString(),
                Title = t.Title,
                Genre = t.Genre ?? "",
                Price = (decimal)t.Price
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

        var totalEarned = (decimal)allPurchases.Sum(p => p.Amount);

        var payouts = await _payouts.GetByCreatorIdAsync(userId);
        var pendingPayouts = (decimal)payouts.Where(p => p.Status == "pending").Sum(p => p.Amount);
        var paidOut = (decimal)payouts.Where(p => p.Status == "completed").Sum(p => p.Amount);

        return new
        {
            totalEarned,
            pendingBalance = totalEarned - paidOut - pendingPayouts,
            pendingPayouts,
            paidOut
        };
    }
}
