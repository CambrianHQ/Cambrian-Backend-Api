using Cambrian.Application.DTOs.Payouts;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

public class PayoutService : IPayoutService
{
    private readonly IPayoutRepository _payouts;
    private readonly IPurchaseRepository _purchases;

    public PayoutService(IPayoutRepository payouts, IPurchaseRepository purchases)
    {
        _payouts = payouts;
        _purchases = purchases;
    }

    public async Task<object> GetEarningsAsync(string creatorId)
    {
        var payouts = await _payouts.GetByCreatorIdAsync(creatorId);
        var completedPayouts = payouts.Where(p => p.Status == "completed").Sum(p => (decimal)p.Amount);
        var pendingPayouts = payouts.Where(p => p.Status is "pending" or "approved").Sum(p => (decimal)p.Amount);

        return new
        {
            balance = completedPayouts,
            pending = pendingPayouts,
            available = completedPayouts - pendingPayouts,
            currency = "USD"
        };
    }

    public async Task<PayoutResponse> RequestAsync(PayoutRequest request, string creatorId)
    {
        if (request.Amount <= 0)
            throw new ArgumentException("Amount must be greater than zero.");

        if (string.IsNullOrWhiteSpace(creatorId))
            throw new InvalidOperationException("Creator identity could not be determined.");

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
}