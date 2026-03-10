using Cambrian.Application.DTOs.Payouts;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

public class PayoutService : IPayoutService
{
    private readonly IPayoutRepository _payouts;
    private readonly IPurchaseRepository _purchases;
    private readonly ITrackRepository _tracks;

    public PayoutService(IPayoutRepository payouts, IPurchaseRepository purchases, ITrackRepository tracks)
    {
        _payouts = payouts;
        _purchases = purchases;
        _tracks = tracks;
    }

    public async Task<object> GetEarningsAsync(string userId)
    {
        // Compute real earnings from completed purchases on the creator's tracks
        var tracks = await _tracks.GetByCreatorIdAsync(userId);
        var allPurchases = new List<Purchase>();
        foreach (var track in tracks)
        {
            var tp = await _purchases.GetByTrackIdAsync(track.Id);
            allPurchases.AddRange(tp.Where(p => p.Status == "completed"));
        }

        var totalEarned = allPurchases.Sum(p => p.AmountCents) / 100m;

        var payouts = await _payouts.GetByCreatorIdAsync(userId);
        var paidOut = (decimal)payouts.Where(p => p.Status == "completed").Sum(p => p.Amount);
        var pendingPayouts = (decimal)payouts.Where(p => p.Status == "pending").Sum(p => p.Amount);
        var available = totalEarned - paidOut - pendingPayouts;

        return new
        {
            available = Math.Max(0, available),
            pending = pendingPayouts,
            totalEarned,
            totalWithdrawn = paidOut
        };
    }

    public async Task<PayoutResponse> RequestAsync(PayoutRequest request, string creatorId)
    {
        if (request.Amount <= 0)
            throw new ArgumentException("Amount must be greater than zero.");

        if (string.IsNullOrWhiteSpace(creatorId))
            throw new ArgumentException("Creator ID is required for payout requests.");

        var payout = new Payout
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            Amount = (double)request.Amount,
            Status = "pending",
            RequestedAt = DateTime.UtcNow
        };

        await _payouts.AddAsync(payout);

        return new PayoutResponse
        {
            Amount = request.Amount,
            Status = "pending"
        };
    }

    public async Task<IReadOnlyCollection<PayoutResponse>> GetHistoryAsync(string userId, int take = 50)
    {
        var payouts = await _payouts.GetByCreatorIdAsync(userId);

        return payouts.Take(take).Select(p => new PayoutResponse
        {
            Amount = (decimal)p.Amount,
            Status = p.Status
        }).ToList();
    }
}