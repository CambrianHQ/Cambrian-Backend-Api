using Cambrian.Application.DTOs.Subscriptions;

namespace Cambrian.Application.Interfaces;

public interface ISubscriptionService
{
    IReadOnlyCollection<SubscriptionPlanResponse> GetPlans();

    Task<CurrentSubscriptionResponse> GetCurrentAsync(string userId);

    Task<CurrentSubscriptionResponse> UpdateAsync(string userId, UpdateSubscriptionRequest request);

    Task CancelAsync(string userId);

    Task<IReadOnlyCollection<SubscriptionResponse>> GetHistoryAsync(string userId);
}
