using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface ISubscriptionRepository
{
    Task<Subscription?> GetActiveAsync(string userId);

    /// <summary>
    /// Flip Status 'active' -> 'expired' for subscriptions past their ExpiresAt.
    /// Returns the number expired. Idempotent; safe to run repeatedly.
    /// </summary>
    Task<int> ExpireLapsedAsync(DateTime nowUtc);

    Task<List<Subscription>> GetHistoryAsync(string userId);

    Task<Subscription> CreateAsync(Subscription subscription);

    Task UpdateAsync(Subscription subscription);

    Task CancelAsync(Guid subscriptionId);
}
