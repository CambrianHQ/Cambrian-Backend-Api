using Cambrian.Application.DTOs.Payouts;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class PayoutService : IPayoutService
{
    public Task<object> GetEarningsAsync()
    {
        return Task.FromResult<object>(new { balance = 125.50m, currency = "USD" });
    }

    public Task<PayoutResponse> RequestAsync(PayoutRequest request)
    {
        var response = new PayoutResponse
        {
            Amount = request.Amount,
            Status = "pending"
        };

        return Task.FromResult(response);
    }
}