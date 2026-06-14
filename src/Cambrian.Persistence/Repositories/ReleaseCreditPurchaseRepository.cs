using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

/// <summary>EF Core data access for <see cref="ReleaseCreditPurchase"/> (thin, owner-scoped).</summary>
public class ReleaseCreditPurchaseRepository : IReleaseCreditPurchaseRepository
{
    private readonly CambrianDbContext _db;

    public ReleaseCreditPurchaseRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<int> SumPaidCreditsAsync(string creatorId, CancellationToken ct = default) =>
        await _db.ReleaseCreditPurchases
            .Where(p => p.CreatorId == creatorId && p.Status == "paid")
            .Select(p => (int?)p.Credits)
            .SumAsync(ct) ?? 0;

    public Task<bool> ExistsForSessionAsync(string stripeSessionId, CancellationToken ct = default) =>
        _db.ReleaseCreditPurchases.AnyAsync(p => p.StripeSessionId == stripeSessionId, ct);

    public async Task AddAsync(ReleaseCreditPurchase purchase, CancellationToken ct = default)
    {
        _db.ReleaseCreditPurchases.Add(purchase);
        await _db.SaveChangesAsync(ct);
    }
}
