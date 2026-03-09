using Cambrian.Application.DTOs.Subscriptions;

namespace Cambrian.Application.Interfaces;

public interface ISubscriptionService
{
    Task<IReadOnlyCollection<PlanResponse>> GetPlansAsync();

    Task<SubscriptionResponse> GetCurrentAsync(string userId);

    Task<SubscriptionResponse> UpdateAsync(UpdateSubscriptionRequest request, string userId);

    Task CancelAsync(string userId);

    Task<IReadOnlyCollection<SubscriptionResponse>> GetHistoryAsync(string userId);
}
