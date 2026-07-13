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
    private static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(5);

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
            Status = "completed",
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

    public async Task<IdempotencyClaim> TryBeginAsync(string key, string userId, string routeKey, string requestHash, TimeSpan? staleAfter = null, CancellationToken ct = default)
    {
        var stale = staleAfter ?? DefaultStaleAfter;

        // First attempt: nobody has claimed this triple yet. The unique index on
        // (Key, UserId, RouteKey) is the real cross-instance concurrency guard —
        // this insert either wins outright or throws, never silently overwrites.
        var now = DateTime.UtcNow;
        var claimAttempt = new ApiIdempotencyKey
        {
            Id = Guid.NewGuid(),
            Key = key,
            UserId = userId,
            RouteKey = routeKey,
            RequestHash = requestHash,
            Status = "processing",
            ResponseBody = string.Empty,
            StatusCode = 0,
            CreatedAt = now,
            ExpiresAt = now.Add(DefaultTtl),
        };

        _db.ApiIdempotencyKeys.Add(claimAttempt);
        try
        {
            await _db.SaveChangesAsync(ct);
            return new IdempotencyClaim(IdempotencyClaimOutcome.Claimed);
        }
        catch (DbUpdateException)
        {
            _db.Entry(claimAttempt).State = EntityState.Detached;
        }

        // Lost the insert race — inspect whoever got there first and decide
        // whether to replay, report in-progress, reject a payload mismatch, or
        // reclaim an abandoned/failed attempt.
        return await ResolveExistingClaimAsync(key, userId, routeKey, requestHash, stale, ct);
    }

    private async Task<IdempotencyClaim> ResolveExistingClaimAsync(string key, string userId, string routeKey, string requestHash, TimeSpan stale, CancellationToken ct)
    {
        var existing = await _db.ApiIdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == key && r.UserId == userId && r.RouteKey == routeKey, ct);

        if (existing is null)
        {
            // The row we lost the race to has since been evicted (expired) —
            // safe to try claiming fresh again, once.
            return await TryBeginAsync(key, userId, routeKey, requestHash, stale, ct);
        }

        if (existing.ExpiresAt <= DateTime.UtcNow)
            return await ReclaimAsync(existing, key, userId, routeKey, requestHash, ct);

        switch (existing.Status)
        {
            case "completed":
                if (!string.IsNullOrEmpty(existing.RequestHash) && existing.RequestHash != requestHash)
                    return new IdempotencyClaim(IdempotencyClaimOutcome.Mismatch);
                return new IdempotencyClaim(IdempotencyClaimOutcome.Completed, existing.StatusCode, existing.ResponseBody);

            case "failed":
                return await ReclaimAsync(existing, key, userId, routeKey, requestHash, ct);

            case "processing":
                if (existing.CreatedAt <= DateTime.UtcNow - stale)
                    return await ReclaimAsync(existing, key, userId, routeKey, requestHash, ct);
                if (!string.IsNullOrEmpty(existing.RequestHash) && existing.RequestHash != requestHash)
                    return new IdempotencyClaim(IdempotencyClaimOutcome.Mismatch);
                return new IdempotencyClaim(IdempotencyClaimOutcome.Processing);

            default:
                return new IdempotencyClaim(IdempotencyClaimOutcome.Processing);
        }
    }

    /// <summary>
    /// Atomically reclaims a stale "processing" or "failed" row via a conditional
    /// UPDATE (not a tracked-entity save) so two callers racing to reclaim the
    /// same abandoned key can't both believe they won.
    /// </summary>
    private async Task<IdempotencyClaim> ReclaimAsync(ApiIdempotencyKey existing, string key, string userId, string routeKey, string requestHash, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ApiIdempotencyKeys"
            SET "Status" = 'processing', "RequestHash" = {requestHash}, "CreatedAt" = {now}, "ExpiresAt" = {now.Add(DefaultTtl)}, "ResponseBody" = '', "StatusCode" = 0
            WHERE "Id" = {existing.Id} AND "Status" IN ('processing', 'failed')
            """, ct);

        if (rows == 1)
            return new IdempotencyClaim(IdempotencyClaimOutcome.Claimed);

        // Lost the reclaim race too — someone else reclaimed or completed it first.
        return await ResolveExistingClaimAsync(key, userId, routeKey, requestHash, DefaultStaleAfter, ct);
    }

    public async Task CompleteAsync(string key, string userId, string routeKey, int statusCode, string responseBody, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ApiIdempotencyKeys"
            SET "Status" = 'completed', "StatusCode" = {statusCode}, "ResponseBody" = {responseBody}, "ExpiresAt" = {now.Add(ttl ?? DefaultTtl)}
            WHERE "Key" = {key} AND "UserId" = {userId} AND "RouteKey" = {routeKey}
            """, ct);
    }

    public async Task MarkFailedAsync(string key, string userId, string routeKey, CancellationToken ct = default)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ApiIdempotencyKeys"
            SET "Status" = 'failed'
            WHERE "Key" = {key} AND "UserId" = {userId} AND "RouteKey" = {routeKey} AND "Status" = 'processing'
            """, ct);
    }
}
