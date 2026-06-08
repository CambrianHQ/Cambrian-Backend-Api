using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class TrackBoostRepository : ITrackBoostRepository
{
    private readonly CambrianDbContext _db;

    public TrackBoostRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public Task<TrackBoost?> GetByUserAndTrackAsync(string userId, Guid trackId) =>
        _db.TrackBoosts.FirstOrDefaultAsync(b => b.UserId == userId && b.TrackId == trackId);

    public async Task AddAsync(TrackBoost boost)
    {
        _db.TrackBoosts.Add(boost);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Concurrent boost won the race; the UNIQUE (UserId, TrackId) index
            // rejected this duplicate. Boosting is idempotent, so treat as success.
            _db.Entry(boost).State = EntityState.Detached;
        }
    }

    public async Task RemoveAsync(Guid id)
    {
        var boost = await _db.TrackBoosts.FindAsync(id);
        if (boost is not null)
        {
            _db.TrackBoosts.Remove(boost);
            await _db.SaveChangesAsync();
        }
    }

    public Task<int> CountByTrackAsync(Guid trackId) =>
        _db.TrackBoosts.CountAsync(b => b.TrackId == trackId);

    // ── Hot This Week (rolling window) ──────────────────────────────────
    // Explicit join (not the b.Track navigation) + anonymous projection with an
    // in-query orderby — the form EF translates to GROUP BY / ORDER BY across
    // relational providers.

    public async Task<IReadOnlyList<RankedTrack>> GetHotSinceAsync(DateTime since, int skip, int take)
    {
        var ranked = await (
                from b in _db.TrackBoosts
                join t in _db.Tracks on b.TrackId equals t.Id
                where b.CreatedAt >= since && t.Visibility == "public"
                group b by b.TrackId into g
                orderby g.Count() descending, g.Key descending // count desc, stable tiebreak
                select new { TrackId = g.Key, Count = g.Count() })
            .Skip(skip)
            .Take(take)
            .ToListAsync();

        if (ranked.Count == 0)
            return Array.Empty<RankedTrack>();

        var ids = ranked.Select(r => r.TrackId).ToList();
        var tracks = await _db.Tracks
            .Include(t => t.Creator)
            .Include(t => t.CreatorEntity)
            .Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id);

        // Preserve the ranked order; drop any track that vanished mid-query.
        return ranked
            .Where(r => tracks.ContainsKey(r.TrackId))
            .Select(r => new RankedTrack(tracks[r.TrackId], r.Count))
            .ToList();
    }

    public async Task<int> CountHotSinceAsync(DateTime since) =>
        await (
                from b in _db.TrackBoosts
                join t in _db.Tracks on b.TrackId equals t.Id
                where b.CreatedAt >= since && t.Visibility == "public"
                group b by b.TrackId into g
                select g.Key)
            .CountAsync();

    public async Task<IReadOnlyCollection<Guid>> GetBoostedTrackIdsAsync(string userId, IReadOnlyCollection<Guid> trackIds)
    {
        if (string.IsNullOrEmpty(userId) || trackIds.Count == 0)
            return Array.Empty<Guid>();

        return await _db.TrackBoosts
            .Where(b => b.UserId == userId && trackIds.Contains(b.TrackId))
            .Select(b => b.TrackId)
            .ToListAsync();
    }
}
