using Cambrian.Application.DTOs.Payouts;

namespace Cambrian.Application.Interfaces;

public interface IPayoutService
{
    Task<object> GetEarningsAsync();

    Task<PayoutResponse> RequestAsync(PayoutRequest request);
}
