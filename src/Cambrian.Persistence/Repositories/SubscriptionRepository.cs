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
        // current and not past its end date. Trialing rows grant the full tier
        // until TrialEndsAt, then fail closed to free if Stripe has not sent the
        // active conversion webhook yet.
        var now = DateTime.UtcNow;
        return await _db.Subscriptions
            .Where(s => s.UserId == userId
                && (
                    (s.Status == "active" && (s.ExpiresAt == null || s.ExpiresAt > now))
                    || (s.Status == "trialing" && ((s.TrialEndsAt ?? s.ExpiresAt) == null || (s.TrialEndsAt ?? s.ExpiresAt) > now))
                ))
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<bool> HasAnyForUserOrCustomerAsync(string userId, string? stripeCustomerId)
    {
        return await _db.Subscriptions.AnyAsync(s =>
            s.UserId == userId
            || (!string.IsNullOrWhiteSpace(stripeCustomerId) && s.StripeCustomerId == stripeCustomerId));
    }

    public async Task<int> ExpireLapsedAsync(DateTime nowUtc)
    {
        // Data-hygiene sweep: flip current subscriptions to 'expired' after their
        // access window passes, so admin/reporting stays truthful. Tier enforcement
        // does NOT depend on this having run (see GetActiveAsync).
        // whose ExpiresAt has passed, so the stored Status stays truthful for
        var lapsed = await _db.Subscriptions
            .Where(s =>
                (s.Status == "active" && s.ExpiresAt != null && s.ExpiresAt <= nowUtc)
                || (s.Status == "trialing" && (s.TrialEndsAt ?? s.ExpiresAt) != null && (s.TrialEndsAt ?? s.ExpiresAt) <= nowUtc))
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
