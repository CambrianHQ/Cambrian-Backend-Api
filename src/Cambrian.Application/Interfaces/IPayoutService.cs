using Cambrian.Application.DTOs.Payouts;

namespace Cambrian.Application.Interfaces;

public interface IPayoutService
{
    Task<object> GetEarningsAsync();

    Task<PayoutResponse> RequestAsync(PayoutRequest request);

    Task<IReadOnlyCollection<PayoutHistoryItemResponse>> GetHistoryAsync(string userId, int take = 50);
}
