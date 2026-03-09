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

    public async Task<object> GetEarningsAsync(string creatorId)
    {
        var tracks = await _tracks.GetByCreatorIdAsync(creatorId);
        var trackIds = tracks.Select(t => t.Id).ToHashSet();

        double totalRevenue = 0;
        foreach (var trackId in trackIds)
        {
            var purchases = await _purchases.GetByTrackIdAsync(trackId);
            totalRevenue += purchases
                .Where(p => p.Status == "completed")
                .Sum(p => p.Amount);
        }

        var payouts = await _payouts.GetByCreatorIdAsync(creatorId);
        var totalPaidOut = payouts
            .Where(p => p.Status == "completed" || p.Status == "approved")
            .Sum(p => p.Amount);
        var totalPending = payouts
            .Where(p => p.Status == "pending")
            .Sum(p => p.Amount);

        var available = totalRevenue - totalPaidOut - totalPending;

        return new
        {
            balance = Math.Round(totalRevenue, 2),
            pending = Math.Round(totalPending, 2),
            paidOut = Math.Round(totalPaidOut, 2),
            available = Math.Round(Math.Max(0, available), 2),
            currency = "USD"
        };
    }

    public async Task<PayoutResponse> RequestAsync(PayoutRequest request, string creatorId)
    {
        if (request.Amount <= 0)
            throw new ArgumentException("Amount must be greater than zero.");

        if (string.IsNullOrWhiteSpace(creatorId))
            throw new ArgumentException("Creator ID is required for payout requests.");

        var tracks = await _tracks.GetByCreatorIdAsync(creatorId);
        var trackIds = tracks.Select(t => t.Id).ToHashSet();

        double totalRevenue = 0;
        foreach (var trackId in trackIds)
        {
            var purchases = await _purchases.GetByTrackIdAsync(trackId);
            totalRevenue += purchases
                .Where(p => p.Status == "completed")
                .Sum(p => p.Amount);
        }

        var payouts = await _payouts.GetByCreatorIdAsync(creatorId);
        var totalPaidOrPending = payouts
            .Where(p => p.Status == "completed" || p.Status == "approved" || p.Status == "pending")
            .Sum(p => p.Amount);

        var available = totalRevenue - totalPaidOrPending;
        if ((double)request.Amount > available)
            throw new InvalidOperationException(
                $"Payout amount {request.Amount:F2} exceeds available balance {available:F2}.");

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
