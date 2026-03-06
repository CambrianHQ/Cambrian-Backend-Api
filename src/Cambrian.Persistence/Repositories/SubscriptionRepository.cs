using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class SubscriptionRepository : ISubscriptionRepository
{
    private readonly CambrianDbContext _db;

    public SubscriptionRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<Subscription?> GetActiveAsync(string userId)
    {
        return await _db.Subscriptions
            .Where(s => s.UserId == userId && s.Status == "active")
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<List<Subscription>> GetHistoryAsync(string userId)
    {
        return await _db.Subscriptions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.StartedAt)
            .ToListAsync();
    }

    public async Task<Subscription> CreateAsync(Subscription subscription)
    {
        _db.Subscriptions.Add(subscription);
        await _db.SaveChangesAsync();
        return subscription;
    }

    public async Task UpdateAsync(Subscription subscription)
    {
        _db.Subscriptions.Update(subscription);
        await _db.SaveChangesAsync();
    }

    public async Task CancelAsync(Guid subscriptionId)
    {
        var subscription = await _db.Subscriptions.FindAsync(subscriptionId);
        if (subscription is not null)
        {
            subscription.Status = "cancelled";
            subscription.ExpiresAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }
}
