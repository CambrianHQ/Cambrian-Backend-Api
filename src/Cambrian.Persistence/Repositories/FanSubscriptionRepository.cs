using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

/// <summary>EF Core data access for <see cref="FanSubscription"/>.</summary>
public class FanSubscriptionRepository : IFanSubscriptionRepository
{
    private readonly CambrianDbContext _db;

    public FanSubscriptionRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public Task<FanSubscription?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.FanSubscriptions.FirstOrDefaultAsync(s => s.Id == id, ct);

    public Task<FanSubscription?> GetByStripeSubscriptionIdAsync(string stripeSubscriptionId, CancellationToken ct = default) =>
        _db.FanSubscriptions.FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId, ct);

    public Task<FanSubscription?> GetLiveByFanAndArtistAsync(string fanUserId, string artistUserId, CancellationToken ct = default) =>
        _db.FanSubscriptions.FirstOrDefaultAsync(
            s => s.FanUserId == fanUserId && s.ArtistUserId == artistUserId && s.Status == "active",
            ct);

    public async Task AddAsync(FanSubscription subscription, CancellationToken ct = default)
    {
        _db.FanSubscriptions.Add(subscription);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(FanSubscription subscription, CancellationToken ct = default)
    {
        _db.FanSubscriptions.Update(subscription);
        await _db.SaveChangesAsync(ct);
    }
}
