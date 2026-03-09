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

    public async Task<object> GetEarningsAsync()
    {
        return await Task.FromResult(new { balance = 0m, pending = 0m, available = 0m, currency = "USD" });
    }

    public async Task<PayoutResponse> RequestAsync(PayoutRequest request, string userId)
    {
        if (request.Amount <= 0)
            throw new ArgumentException("Amount must be greater than zero.");

        var payout = new Payout
        {
            Id = Guid.NewGuid(),
            CreatorId = userId,
            Amount = (double)request.Amount,
            Status = "pending"
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
