using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class StreamRepository : IStreamRepository
{
    /// <summary>
    /// Repeat starts by the same user on the same track within this window collapse into a
    /// single play, so refreshes / replays / double-clicks can't inflate play counts.
    /// </summary>
    private const int DebounceWindowSeconds = 30;

    private readonly CambrianDbContext _db;

    public StreamRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<StreamSession> StartAsync(Guid trackId, string? userId)
    {
        // Debounce identified users (anonymous plays can't be attributed, so they're not deduped).
        if (!string.IsNullOrEmpty(userId))
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-DebounceWindowSeconds);
            var recent = await _db.StreamSessions
                .Where(s => s.TrackId == trackId && s.UserId == userId && s.StartedAt >= cutoff)
                .OrderByDescending(s => s.StartedAt)
                .FirstOrDefaultAsync();
            if (recent is not null)
                return recent;
        }

        var track = await _db.Tracks.FindAsync(trackId);

        var session = new StreamSession
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            UserId = userId,
            Title = track?.Title,
            StartedAt = DateTime.UtcNow
        };

        _db.StreamSessions.Add(session);
        await _db.SaveChangesAsync();

        return session;
    }

    public async Task StopAsync(Guid sessionId)
    {
        var session = await _db.StreamSessions.FindAsync(sessionId);

        if (session is not null)
        {
            session.StoppedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task<StreamSession?> GetByIdAsync(Guid id)
    {
        return await _db.StreamSessions
            .Include(s => s.Track)
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<Dictionary<Guid, int>> GetPlayCountsByTrackIdsAsync(IEnumerable<Guid> trackIds)
    {
        var trackIdSet = trackIds.ToHashSet();
        return await _db.StreamSessions
            .Where(s => trackIdSet.Contains(s.TrackId))
            .GroupBy(s => s.TrackId)
            .Select(g => new { TrackId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TrackId, x => x.Count);
    }
}
