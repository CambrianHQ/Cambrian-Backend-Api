using Cambrian.Application.DTOs.Payouts;

namespace Cambrian.Application.Interfaces;

public interface IPayoutService
{
    Task<object> GetEarningsAsync(string creatorId);

    Task<PayoutResponse> RequestAsync(PayoutRequest request, string creatorId);

    Task<IReadOnlyCollection<PayoutResponse>> GetHistoryAsync(string userId, int take = 50);
}
