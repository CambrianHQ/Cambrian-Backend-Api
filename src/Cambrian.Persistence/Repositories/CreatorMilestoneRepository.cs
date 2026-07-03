using Cambrian.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public sealed class CreatorMilestoneRepository : ICreatorMilestoneRepository
{
    private readonly CambrianDbContext _db;

    public CreatorMilestoneRepository(CambrianDbContext db) => _db = db;

    public async Task<FirstPlayMilestone?> GetFirstPlayAsync(string userId, CancellationToken ct = default)
    {
        // Anonymous sessions count: StreamSession.UserId being null is irrelevant —
        // the milestone belongs to the CREATOR, keyed by track ownership.
        var first = await _db.StreamSessions
            .AsNoTracking()
            .Join(
                _db.Tracks.Where(t => t.CreatorId == userId),
                s => s.TrackId,
                t => t.Id,
                (s, t) => new { s.StartedAt, s.TrackId })
            .OrderBy(x => x.StartedAt)
            .FirstOrDefaultAsync(ct);

        return first is null
            ? null
            : new FirstPlayMilestone { AtUtc = first.StartedAt, TrackId = first.TrackId };
    }

    public async Task<FirstFanMilestone?> GetFirstFanEventAsync(string userId, CancellationToken ct = default)
    {
        // Earliest of each fan signal, compared in memory (four tiny scalar
        // queries). Only timestamps + a source label leave this method — the
        // fan's identity never does.
        var creatorGuids = await _db.Creators
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var trackIds = await _db.Tracks
            .AsNoTracking()
            .Where(t => t.CreatorId == userId)
            .Select(t => t.Id)
            .ToListAsync(ct);

        DateTime? firstFollow = creatorGuids.Count == 0
            ? null
            : await _db.CreatorFollows.AsNoTracking()
                .Where(f => creatorGuids.Contains(f.CreatorId))
                .OrderBy(f => f.CreatedAt)
                .Select(f => (DateTime?)f.CreatedAt)
                .FirstOrDefaultAsync(ct);

        DateTime? firstSave = trackIds.Count == 0
            ? null
            : await _db.TrackBoosts.AsNoTracking()
                .Where(b => trackIds.Contains(b.TrackId))
                .OrderBy(b => b.CreatedAt)
                .Select(b => (DateTime?)b.CreatedAt)
                .FirstOrDefaultAsync(ct);

        DateTime? firstSupport = trackIds.Count == 0
            ? null
            : await _db.Purchases.AsNoTracking()
                .Where(p => trackIds.Contains(p.TrackId) && p.Status == "completed")
                .OrderBy(p => p.CreatedAt)
                .Select(p => (DateTime?)p.CreatedAt)
                .FirstOrDefaultAsync(ct);

        DateTime? firstSubscription = await _db.FanSubscriptions.AsNoTracking()
            .Where(s => s.ArtistUserId == userId && s.Status == "active")
            .OrderBy(s => s.CreatedAt)
            .Select(s => (DateTime?)(s.ActivatedAt ?? s.CreatedAt))
            .FirstOrDefaultAsync(ct);

        var candidates = new List<(DateTime At, string Source)>();
        if (firstFollow is DateTime f) candidates.Add((f, "follow"));
        if (firstSave is DateTime b) candidates.Add((b, "save"));
        if (firstSupport is DateTime p) candidates.Add((p, "support"));
        if (firstSubscription is DateTime s2) candidates.Add((s2, "subscription"));

        if (candidates.Count == 0) return null;
        var earliest = candidates.OrderBy(c => c.At).First();
        return new FirstFanMilestone { AtUtc = earliest.At, Source = earliest.Source };
    }
}
