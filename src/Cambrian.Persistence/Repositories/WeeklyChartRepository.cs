using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public sealed class WeeklyChartRepository : IWeeklyChartRepository
{
    private const long WeeklyChartAdvisoryLockNamespace = 0x5743484152540000L;

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
        var rows = await _db.QualifiedPlayEvents
            .AsNoTracking()
            .Where(p => p.QualifiedAtUtc >= fromUtc && p.QualifiedAtUtc < toUtc)
            .Join(
                EligibleTracks(),
                p => p.TrackId,
                t => t.Id,
                (p, t) => p.TrackId)
            .GroupBy(id => id)
            .Select(g => new { TrackId = g.Key, Plays = g.LongCount() })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.TrackId,
            r => r.Plays > int.MaxValue ? int.MaxValue : (int)r.Plays);
    }

    public async Task<IReadOnlyList<WeeklyChartCandidate>> GetEligibleCandidatesAsync(
        DateTime fromUtc,
        DateTime toUtc,
        DateTime dataThroughUtc,
        CancellationToken ct = default)
    {
        var trackRows = await (
            from track in EligibleTracks()
            join stats in _db.TrackStats.AsNoTracking()
                on track.Id equals stats.TrackId into statsGroup
            from stats in statsGroup.DefaultIfEmpty()
            select new
            {
                track.Id,
                track.CreatorId,
                track.Title,
                Artist = track.CreatorEntity != null
                    ? track.CreatorEntity.DisplayName
                        ?? track.CreatorEntity.Username
                        ?? track.Creator.DisplayName
                        ?? "Unknown Artist"
                    : track.Creator.DisplayName ?? "Unknown Artist",
                track.CoverArtUrl,
                track.CreatedAt,
                LifetimePlays = stats == null ? 0L : stats.PlayCount,
            })
            .ToListAsync(ct);

        var weeklyRows = await (
            from play in _db.QualifiedPlayEvents.AsNoTracking()
            join track in EligibleTracks() on play.TrackId equals track.Id
            where play.QualifiedAtUtc >= fromUtc
                && play.QualifiedAtUtc < toUtc
                && play.QualifiedAtUtc <= dataThroughUtc
            group play by play.TrackId into grouped
            select new
            {
                TrackId = grouped.Key,
                Plays = grouped.LongCount(),
            })
            .ToListAsync(ct);

        var weeklyByTrack = weeklyRows.ToDictionary(x => x.TrackId, x => x.Plays);
        return trackRows
            .Select(track => new WeeklyChartCandidate(
                track.Id,
                track.CreatorId,
                track.Title,
                track.Artist,
                track.CoverArtUrl,
                track.CreatedAt,
                weeklyByTrack.GetValueOrDefault(track.Id),
                track.LifetimePlays))
            .ToList();
    }

    public Task<bool> HasQualifiedPlaysAfterAsync(
        DateTime fromUtc,
        DateTime toUtc,
        DateTime dataThroughUtc,
        DateTime observedAtUtc,
        CancellationToken ct = default)
    {
        return _db.QualifiedPlayEvents
            .AsNoTracking()
            .Where(play => play.QualifiedAtUtc >= fromUtc
                && play.QualifiedAtUtc < toUtc
                && play.QualifiedAtUtc > dataThroughUtc
                && play.QualifiedAtUtc <= observedAtUtc)
            .Join(
                EligibleTracks(),
                play => play.TrackId,
                track => track.Id,
                (play, track) => play.Id)
            .AnyAsync(ct);
    }

    public async Task ReplaceWeekAsync(DateTime weekStartUtc, IReadOnlyList<WeeklyChartSnapshot> rows, CancellationToken ct = default)
    {
        // Transactional delete + insert = idempotent recompute for a week.
        // The InMemory test provider has no transactions; SQLite/Postgres do.
        var supportsTransactions = _db.Database.IsRelational();

        if (supportsTransactions)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await AcquirePostgresWeekLockAsync(weekStartUtc, ct);
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
        var incomingComputedAt = rows.Count == 0
            ? (DateTime?)null
            : rows.Max(row => row.ComputedAtUtc);
        var incomingDataThrough = rows.Count == 0
            ? (DateTime?)null
            : rows.Max(row => row.DataThroughUtc);
        var existingVersion = await _db.WeeklyChartSnapshots
            .Where(snapshot => snapshot.WeekStartUtc == weekStartUtc)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                DataThrough = group.Max(snapshot => snapshot.DataThroughUtc),
                ComputedAt = group.Max(snapshot => snapshot.ComputedAtUtc),
            })
            .SingleOrDefaultAsync(ct);

        // Two app instances can calculate concurrently. The PostgreSQL advisory
        // transaction lock serializes replacement, and this guard prevents an
        // older event watermark that finishes later from regressing chart data.
        // ComputedAt breaks ties only when both snapshots cover the same events.
        if (existingVersion is not null
            && (incomingDataThrough is null
                || (existingVersion.DataThrough is not null
                    && (incomingDataThrough.Value < existingVersion.DataThrough.Value
                        || (incomingDataThrough.Value == existingVersion.DataThrough.Value
                            && incomingComputedAt < existingVersion.ComputedAt)))))
        {
            return;
        }

        var existing = await _db.WeeklyChartSnapshots
            .Where(s => s.WeekStartUtc == weekStartUtc)
            .ToListAsync(ct);
        _db.WeeklyChartSnapshots.RemoveRange(existing);
        await _db.WeeklyChartSnapshots.AddRangeAsync(rows, ct);
        await _db.SaveChangesAsync(ct);
    }

    private IQueryable<Track> EligibleTracks()
    {
        return _db.Tracks
            .AsNoTracking()
            .Where(track => track.Visibility == "public"
                && (track.Status == "available" || track.Status == "active")
                && track.DeletedAt == null
                && track.PurgeRequestedAt == null
                && track.PurgedAt == null
                && !track.ExclusiveSold
                && track.AudioUrl != null
                && track.AudioUrl.Trim() != string.Empty);
    }

    private async Task AcquirePostgresWeekLockAsync(DateTime weekStartUtc, CancellationToken ct)
    {
        if (_db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        var lockKey = WeeklyChartAdvisoryLockNamespace ^ weekStartUtc.Ticks;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            ct);
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
