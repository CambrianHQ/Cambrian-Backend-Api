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
        // EXPIRY ENFORCEMENT: a subscription only grants a tier while it is both
        // Status='active' AND not past ExpiresAt. A null ExpiresAt means no set
        // end (an ongoing Stripe sub) and stays active. This is the read-time
        // enforcement — the moment ExpiresAt passes, /auth/me resolves the user
        // back down to free, with no reliance on a sweep having run yet.
        var now = DateTime.UtcNow;
        return await _db.Subscriptions
            .Where(s => s.UserId == userId
                && s.Status == "active"
                && (s.ExpiresAt == null || s.ExpiresAt > now))
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<int> ExpireLapsedAsync(DateTime nowUtc)
    {
        // Data-hygiene sweep: flip Status 'active' -> 'expired' for subscriptions
        // whose ExpiresAt has passed, so the stored Status stays truthful for
        // admin/reporting and any other Status=='active' query. Tier enforcement
        // does NOT depend on this having run (see GetActiveAsync).
        var lapsed = await _db.Subscriptions
            .Where(s => s.Status == "active" && s.ExpiresAt != null && s.ExpiresAt <= nowUtc)
            .ToListAsync();
        if (lapsed.Count == 0) return 0;
        foreach (var s in lapsed) s.Status = "expired";
        await _db.SaveChangesAsync();
        return lapsed.Count;
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
