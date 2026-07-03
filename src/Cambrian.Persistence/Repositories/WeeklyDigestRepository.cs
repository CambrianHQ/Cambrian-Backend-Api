using Cambrian.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public sealed class WeeklyDigestRepository : IWeeklyDigestRepository
{
    private readonly CambrianDbContext _db;

    public WeeklyDigestRepository(CambrianDbContext db) => _db = db;

    public async Task<IReadOnlyList<DigestCandidate>> GetCreatorCandidatesAsync(CancellationToken ct = default)
    {
        // Audience: users owning at least one public track. The skip policy
        // (unverified / opted-out / already sent) is applied by the service so
        // the run summary can report WHY someone was skipped.
        return await _db.Users
            .AsNoTracking()
            .Where(u => u.Email != null && _db.Tracks.Any(t => t.CreatorId == u.Id && t.Visibility == "public"))
            .Select(u => new DigestCandidate
            {
                UserId = u.Id,
                Email = u.Email!,
                DisplayName = u.DisplayName ?? u.UserName ?? u.Email!,
                EmailVerified = u.EmailVerified,
                WeeklyDigestOptOut = u.WeeklyDigestOptOut,
                LastWeeklyDigestAtUtc = u.LastWeeklyDigestAtUtc,
            })
            .ToListAsync(ct);
    }

    public async Task<DigestWeeklyNumbers> GetWeeklyNumbersAsync(string userId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var playRows = await _db.StreamSessions
            .AsNoTracking()
            .Where(s => s.StartedAt >= fromUtc && s.StartedAt < toUtc)
            .Join(
                _db.Tracks.Where(t => t.CreatorId == userId),
                s => s.TrackId,
                t => t.Id,
                (s, t) => new { t.Id, t.Title })
            .GroupBy(x => new { x.Id, x.Title })
            .Select(g => new { g.Key.Title, Plays = g.Count() })
            .OrderByDescending(x => x.Plays)
            .ToListAsync(ct);

        var creatorGuids = await _db.Creators
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var newFollowers = creatorGuids.Count == 0
            ? 0
            : await _db.CreatorFollows
                .AsNoTracking()
                .CountAsync(f => creatorGuids.Contains(f.CreatorId) && f.CreatedAt >= fromUtc && f.CreatedAt < toUtc, ct);

        var top = playRows.FirstOrDefault();
        return new DigestWeeklyNumbers
        {
            Plays = playRows.Sum(r => r.Plays),
            NewFollowers = newFollowers,
            TopTrackTitle = top?.Title,
            TopTrackPlays = top?.Plays ?? 0,
        };
    }

    public async Task MarkDigestSentAsync(string userId, DateTime sentAtUtc, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return;
        user.LastWeeklyDigestAtUtc = sentAtUtc;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> SetDigestOptOutAsync(string userId, bool optOut, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return false;
        user.WeeklyDigestOptOut = optOut;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
