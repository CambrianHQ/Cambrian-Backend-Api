using Cambrian.Application.Interfaces.V1;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IIdempotencyStore"/>. Persists rows
/// in <c>ApiIdempotencyKeys</c>. Cheap reads (composite-indexed lookup),
/// fire-and-forget writes from the caller's perspective (single insert).
/// </summary>
public sealed class IdempotencyStore : IIdempotencyStore
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    private readonly CambrianDbContext _db;

    public IdempotencyStore(CambrianDbContext db) => _db = db;

    public async Task<IdempotentResponse?> TryGetAsync(string key, string userId, string routeKey, CancellationToken ct = default)
    {
        var row = await _db.ApiIdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Key == key && r.UserId == userId && r.RouteKey == routeKey,
                ct);

        if (row is null) return null;

        if (row.ExpiresAt <= DateTime.UtcNow)
        {
            // Expired — evict so a future retry with the same key starts fresh.
            // Best-effort delete; concurrent eviction is fine (PK conflict swallowed).
            try
            {
                _db.ApiIdempotencyKeys.Remove(new ApiIdempotencyKey { Id = row.Id });
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException) { /* lost race, fine */ }
            return null;
        }

        return new IdempotentResponse(row.StatusCode, row.ResponseBody);
    }

    public async Task SaveAsync(string key, string userId, string routeKey, int statusCode, string responseBody, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var entry = new ApiIdempotencyKey
        {
            Id = Guid.NewGuid(),
            Key = key,
            UserId = userId,
            RouteKey = routeKey,
            StatusCode = statusCode,
            ResponseBody = responseBody,
            CreatedAt = now,
            ExpiresAt = now.Add(ttl ?? DefaultTtl),
        };

        _db.ApiIdempotencyKeys.Add(entry);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Race: another thread already wrote this triple. Idempotent by
            // definition — both writes converge on the same response, so this
            // is fine. Caller will read the winning row on next TryGet.
        }
    }
}
