using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public sealed class EntitlementRepository : IEntitlementRepository
{
    private readonly CambrianDbContext _db;

    public EntitlementRepository(CambrianDbContext db) => _db = db;

    public async Task<Entitlement> AddAsync(Entitlement row, CancellationToken ct = default)
    {
        _db.Entitlements.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public Task<Entitlement?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _db.Entitlements.FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task UpdateAsync(Entitlement row, CancellationToken ct = default)
    {
        _db.Entitlements.Update(row);
        await _db.SaveChangesAsync(ct);
    }

    public Task<List<Entitlement>> GetForUserResourceAsync(
        string userId,
        EntitlementResourceType resourceType,
        string resourceId,
        CancellationToken ct = default)
        => _db.Entitlements
            .AsNoTracking()
            .Where(e => e.UserId == userId
                     && e.ResourceType == resourceType
                     && e.ResourceId == resourceId)
            .ToListAsync(ct);

    public Task<List<Entitlement>> GetForUserAsync(
        string userId,
        EntitlementResourceType? resourceType = null,
        bool includeRevoked = false,
        CancellationToken ct = default)
    {
        var q = _db.Entitlements.AsNoTracking().Where(e => e.UserId == userId);
        if (resourceType is { } rt)
            q = q.Where(e => e.ResourceType == rt);
        if (!includeRevoked)
            q = q.Where(e => e.RevokedAt == null);
        return q.OrderByDescending(e => e.GrantedAt).ToListAsync(ct);
    }
}
