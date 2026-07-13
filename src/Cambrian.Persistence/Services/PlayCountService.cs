using Cambrian.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Cambrian.Persistence.Services;

/// <summary>
/// Reads the TrackStats/CreatorStats projection — never StreamSessions directly — so every
/// caller sees the same numbers. The projection is kept current by StreamRepository's
/// transactional write path and self-healed by PlayCountReconciliationService; Postgres is
/// always the fallback of record.
///
/// Per-track/per-creator reads are a single indexed row lookup, cheap enough to always read
/// live — caching them would mean a play that just landed transactionally could still read back
/// as stale for the cache's TTL, which defeats the point of the transactional write path. The
/// one read worth caching is the platform-wide total (a real SUM across every track); that cache
/// is a best-effort optimization, not a source of truth: this stack has no Redis (see
/// CLAUDE.md), and even so, any exception from the cache (a broken implementation, a future swap
/// to a distributed cache that's temporarily down) is caught and treated as a miss — the read
/// always falls through to Postgres rather than failing or returning incorrect data.
/// </summary>
public sealed class PlayCountService : IPlayCountService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly CambrianDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PlayCountService> _logger;

    public PlayCountService(CambrianDbContext db, IMemoryCache cache, ILogger<PlayCountService> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task<long> GetTrackPlayCountAsync(Guid trackId, CancellationToken ct = default) =>
        await _db.TrackStats.AsNoTracking()
            .Where(s => s.TrackId == trackId)
            .Select(s => s.PlayCount)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, long>> GetTrackPlayCountsAsync(
        IReadOnlyCollection<Guid> trackIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, long>(trackIds.Count);
        foreach (var id in trackIds)
            result[id] = 0L;

        if (trackIds.Count == 0)
            return result;

        var ids = trackIds as IList<Guid> ?? trackIds.ToList();
        var rows = await _db.TrackStats.AsNoTracking()
            .Where(s => ids.Contains(s.TrackId))
            .Select(s => new { s.TrackId, s.PlayCount })
            .ToListAsync(ct);

        foreach (var row in rows)
            result[row.TrackId] = row.PlayCount;

        return result;
    }

    public async Task<long> GetTrackUniqueListenerCountAsync(Guid trackId, CancellationToken ct = default) =>
        await _db.TrackStats.AsNoTracking()
            .Where(s => s.TrackId == trackId)
            .Select(s => s.UniqueListenerCount)
            .FirstOrDefaultAsync(ct);

    public async Task<long> GetCreatorTotalPlaysAsync(string legacyUserId, Guid? creatorUuid, CancellationToken ct = default)
    {
        if (creatorUuid.HasValue)
        {
            var row = await _db.CreatorStats.AsNoTracking()
                .Where(s => s.CreatorId == creatorUuid.Value)
                .Select(s => (long?)s.TotalPlays)
                .FirstOrDefaultAsync(ct);
            if (row.HasValue)
                return row.Value;
        }

        // No CreatorStats row yet — either the projection/backfill hasn't caught up for this
        // creator, or they have no Creator row at all (legacy-only). Live-compute so the total
        // is never silently reported as zero; uses the exact same "Qualified" definition as
        // every other read path, just without the cached projection.
        return await _db.StreamSessions.AsNoTracking()
            .CountAsync(s => s.Qualified && _db.Tracks.Any(t =>
                (t.CreatorId == legacyUserId || (creatorUuid != null && t.CreatorUuid == creatorUuid))
                && t.Id == s.TrackId), ct);
    }

    public Task<long> GetPlatformTotalPlaysAsync(CancellationToken ct = default)
        => GetCachedOrComputeAsync(
            "playcount:platform:total",
            async () => await _db.TrackStats.AsNoTracking().SumAsync(s => s.PlayCount, ct),
            ct);

    private async Task<long> GetCachedOrComputeAsync(string cacheKey, Func<Task<long>> compute, CancellationToken ct)
    {
        try
        {
            return await _cache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheTtl;
                return await compute();
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "EVENT: PlayCountCacheUnavailable key:{Key} — falling back to Postgres directly.",
                cacheKey);
            return await compute();
        }
    }
}
