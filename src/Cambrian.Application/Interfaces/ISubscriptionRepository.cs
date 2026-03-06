using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface ISubscriptionRepository
{
    Task<Subscription?> GetActiveAsync(string userId);

    Task<List<Subscription>> GetHistoryAsync(string userId);

    Task<Subscription> CreateAsync(Subscription subscription);

    Task UpdateAsync(Subscription subscription);

    Task CancelAsync(Guid subscriptionId);
}
