using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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
        // Will be scoped to user once we thread ClaimsPrincipal through
        return await Task.FromResult(new { balance = 0m, pending = 0m, available = 0m, currency = "USD" });
    }

    public async Task<PayoutResponse> RequestAsync(PayoutRequest request)
    {
        if (request.Amount <= 0)
            throw new ArgumentException("Amount must be greater than zero.");

        var payout = new Payout
        {
            Id = Guid.NewGuid(),
            // CreatorId will be set by controller once we add user context
            CreatorId = "",
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
}