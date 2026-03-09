using Cambrian.Application.DTOs.Subscriptions;

namespace Cambrian.Application.Interfaces;

public interface ISubscriptionService
{
    Task<List<PlanResponse>> GetPlansAsync();

    Task<SubscriptionResponse> GetCurrentAsync(string userId);

    Task<SubscriptionResponse> UpdateAsync(string plan, string userId);

    Task CancelAsync(string userId);

    Task<List<SubscriptionResponse>> GetHistoryAsync(string userId);
}
