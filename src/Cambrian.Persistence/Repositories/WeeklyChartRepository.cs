using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public sealed class WeeklyChartRepository : IWeeklyChartRepository
{
    private readonly CambrianDbContext _db;

    public WeeklyChartRepository(CambrianDbContext db) => _db = db;

    public async Task<IReadOnlyList<WeeklyChartSnapshot>> GetWeekAsync(DateTime weekStartUtc, CancellationToken ct = default)
    {
        return await _db.WeeklyChartSnapshots
            .AsNoTracking()
            .Where(s => s.WeekStartUtc == weekStartUtc)
            .OrderBy(s => s.Rank)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WeeklyChartSnapshot>> GetLatestWeekAsync(CancellationToken ct = default)
    {
        var latestWeek = await _db.WeeklyChartSnapshots
            .AsNoTracking()
            .OrderByDescending(s => s.WeekStartUtc)
            .Select(s => (DateTime?)s.WeekStartUtc)
            .FirstOrDefaultAsync(ct);

        if (latestWeek is null) return Array.Empty<WeeklyChartSnapshot>();
        return await GetWeekAsync(latestWeek.Value, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetTrackPlaysInWindowAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var rows = await _db.StreamSessions
            .AsNoTracking()
            .Where(s => s.Qualified && s.StartedAt >= fromUtc && s.StartedAt < toUtc)
            .Join(
                _db.Tracks.Where(t => t.Visibility == "public"),
                s => s.TrackId,
                t => t.Id,
                (s, t) => s.TrackId)
            .GroupBy(id => id)
            .Select(g => new { TrackId = g.Key, Plays = g.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.TrackId, r => r.Plays);
    }

    public async Task ReplaceWeekAsync(DateTime weekStartUtc, IReadOnlyList<WeeklyChartSnapshot> rows, CancellationToken ct = default)
    {
        // Transactional delete + insert = idempotent recompute for a week.
        // The InMemory test provider has no transactions; SQLite/Postgres do.
        var supportsTransactions = _db.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) != true;

        if (supportsTransactions)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await ReplaceWeekCoreAsync(weekStartUtc, rows, ct);
            await tx.CommitAsync(ct);
        }
        else
        {
            await ReplaceWeekCoreAsync(weekStartUtc, rows, ct);
        }
    }

    private async Task ReplaceWeekCoreAsync(DateTime weekStartUtc, IReadOnlyList<WeeklyChartSnapshot> rows, CancellationToken ct)
    {
        var existing = await _db.WeeklyChartSnapshots
            .Where(s => s.WeekStartUtc == weekStartUtc)
            .ToListAsync(ct);
        _db.WeeklyChartSnapshots.RemoveRange(existing);
        await _db.WeeklyChartSnapshots.AddRangeAsync(rows, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DateTime>> ListWeekStartsAsync(int limit, CancellationToken ct = default)
    {
        var capped = Math.Clamp(limit, 1, 520);
        return await _db.WeeklyChartSnapshots
            .AsNoTracking()
            .Select(s => s.WeekStartUtc)
            .Distinct()
            .OrderByDescending(w => w)
            .Take(capped)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WeeklyChartSnapshot>> GetTopRowsForWeeksAsync(IReadOnlyCollection<DateTime> weekStartsUtc, CancellationToken ct = default)
    {
        if (weekStartsUtc.Count == 0) return Array.Empty<WeeklyChartSnapshot>();
        return await _db.WeeklyChartSnapshots
            .AsNoTracking()
            .Where(s => s.Rank == 1 && weekStartsUtc.Contains(s.WeekStartUtc))
            .OrderByDescending(s => s.WeekStartUtc)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetUsernamesByUserIdsAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return new Dictionary<string, string>();
        var rows = await _db.Creators
            .AsNoTracking()
            .Where(c => userIds.Contains(c.UserId) && c.Username != "")
            .Select(c => new { c.UserId, c.Username })
            .ToListAsync(ct);
        // A user id can theoretically map to at most one creator row (unique FK),
        // but guard against duplicates rather than throwing on ToDictionary.
        var map = new Dictionary<string, string>(rows.Count);
        foreach (var row in rows) map[row.UserId] = row.Username;
        return map;
    }
}
