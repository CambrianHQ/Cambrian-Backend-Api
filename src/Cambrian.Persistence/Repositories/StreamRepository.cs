using System.Security.Cryptography;
using System.Text;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cambrian.Persistence.Repositories;

public class StreamRepository : IStreamRepository
{
    /// <summary>
    /// Authenticated plays are bucketed into windows this wide: repeat starts by the same user on
    /// the same track within a window collapse into a single durable event (refreshes, replays,
    /// double-clicks can't inflate play counts). A fixed bucket — rather than the old "is there
    /// anything in the last 30s" sliding-window read — is what makes this enforceable as a real,
    /// deterministic database constraint instead of a read-then-write race.
    /// </summary>
    private const int UserDebounceWindowSeconds = 30;

    /// <summary>Anonymous plays (no user to attribute to) get a wider window, matching the old per-IP rate limit.</summary>
    private static readonly TimeSpan AnonymousDebounceWindow = TimeSpan.FromHours(1);

    private readonly CambrianDbContext _db;
    private readonly ILogger<StreamRepository> _logger;
    private readonly int _minQualifyingSeconds;

    public StreamRepository(CambrianDbContext db, IConfiguration configuration, ILogger<StreamRepository> logger)
    {
        _db = db;
        _logger = logger;
        _minQualifyingSeconds = configuration.GetValue("PlayCounts:MinQualifyingSeconds", 0);
    }

    public async Task<(StreamSession Session, bool IsNewPlay)> StartAsync(Guid trackId, string? userId, string? clientKey)
    {
        var anonymousKey = string.IsNullOrEmpty(userId) ? HashAnonymousKey(clientKey) : null;
        var idempotencyKey = BuildIdempotencyKey(trackId, userId, anonymousKey);

        // Fast path: this exact play attempt was already durably recorded (by this request's own
        // retry, or by another replica). No new row, no counter change — just hand back the
        // winner. This also covers process-restart safety: the row lives in Postgres, not memory.
        var existing = await _db.StreamSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.IdempotencyKey == idempotencyKey);
        if (existing is not null)
            return (existing, false);

        var track = await _db.Tracks.FindAsync(trackId);

        // No minimum listen duration configured (the default) ⇒ every session qualifies the
        // instant it starts, matching the platform's historical "every play counts" behavior.
        var qualifies = _minQualifyingSeconds <= 0;

        var session = new StreamSession
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            UserId = userId,
            Title = track?.Title,
            StartedAt = DateTime.UtcNow,
            IdempotencyKey = idempotencyKey,
            Qualified = qualifies,
            AnonymousKey = anonymousKey,
        };

        _db.StreamSessions.Add(session);
        if (qualifies && track is not null)
            await StageProjectionIncrementAsync(track);

        try
        {
            await _db.SaveChangesAsync();
            return (session, true);
        }
        catch (DbUpdateException)
        {
            // Lost the race: another concurrent request (this replica or another) computed the
            // same idempotency key and its insert committed first. The UNIQUE index rejected ours
            // — that is the durability guarantee working, not a failure. Discard our attempt
            // (including any staged TrackStats/CreatorStats increment — SaveChanges rolled back
            // the whole batch together) and hand back whichever row actually won.
            _db.ChangeTracker.Clear();
            var winner = await _db.StreamSessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.IdempotencyKey == idempotencyKey);
            if (winner is not null)
                return (winner, false);
            throw;
        }
    }

    public async Task StopAsync(Guid sessionId)
    {
        var session = await _db.StreamSessions.FindAsync(sessionId);
        if (session is null)
            return;

        session.StoppedAt = DateTime.UtcNow;

        // Duration-threshold qualification: only relevant when PlayCounts:MinQualifyingSeconds is
        // configured above zero (off by default — see StartAsync, where everything qualifies
        // immediately instead). A session not yet qualified becomes qualified here once the
        // observed listen duration clears the bar, and the projection is incremented once, then.
        if (!session.Qualified && _minQualifyingSeconds > 0)
        {
            var listenedSeconds = (session.StoppedAt.Value - session.StartedAt).TotalSeconds;
            if (listenedSeconds >= _minQualifyingSeconds)
            {
                session.Qualified = true;
                var track = await _db.Tracks.FindAsync(session.TrackId);
                if (track is not null)
                    await StageProjectionIncrementAsync(track);
            }
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Qualified is a concurrency token (see CambrianDbContext): another concurrent
            // StopAsync call for this same session already flipped it false→true and its
            // increment already landed. Don't double-count — just drop our redundant attempt.
            _db.ChangeTracker.Clear();
        }
    }

    public async Task<StreamSession?> GetByIdAsync(Guid id)
    {
        return await _db.StreamSessions
            .Include(s => s.Track)
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    /// <summary>
    /// Stages a +1 to the track's (and, when resolvable, the owning creator's) lifetime play
    /// counter as part of the caller's pending SaveChanges — never its own separate transaction,
    /// so a qualifying event and its counter update commit or roll back together. This is a
    /// best-effort fast-path increment, not the source of truth: PlayCountReconciliationService
    /// recomputes both counters straight from qualified StreamSessions and corrects any drift
    /// (e.g. from two of these increments racing on the same row), so a rare lost update here is
    /// self-healing rather than a correctness bug.
    /// </summary>
    private async Task StageProjectionIncrementAsync(Track track)
    {
        var now = DateTime.UtcNow;

        var trackStat = await _db.TrackStats.FindAsync(track.Id);
        if (trackStat is null)
        {
            trackStat = new TrackStat { TrackId = track.Id };
            _db.TrackStats.Add(trackStat);
        }
        trackStat.PlayCount += 1;
        trackStat.LastPlayedAt = now;
        trackStat.UpdatedAt = now;

        if (track.CreatorUuid.HasValue)
        {
            var creatorStat = await _db.CreatorStats.FindAsync(track.CreatorUuid.Value);
            if (creatorStat is null)
            {
                creatorStat = new CreatorStat { CreatorId = track.CreatorUuid.Value };
                _db.CreatorStats.Add(creatorStat);
            }
            creatorStat.TotalPlays += 1;
            creatorStat.UpdatedAt = now;
        }
    }

    private string BuildIdempotencyKey(Guid trackId, string? userId, string? anonymousKey)
    {
        if (!string.IsNullOrEmpty(userId))
        {
            var bucket = FloorTo(DateTime.UtcNow, TimeSpan.FromSeconds(UserDebounceWindowSeconds));
            return $"user:{userId}:{trackId:D}:{bucket:O}";
        }

        var hourBucket = FloorTo(DateTime.UtcNow, AnonymousDebounceWindow);
        return $"anon:{anonymousKey ?? "unknown"}:{trackId:D}:{hourBucket:O}";
    }

    private static DateTime FloorTo(DateTime value, TimeSpan window)
    {
        var ticks = value.Ticks - (value.Ticks % window.Ticks);
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>
    /// Anonymous listeners are identified by a salted hash of their client key (IP), never the
    /// raw value — this is durability/uniqueness plumbing, not a listener-tracking feature.
    /// </summary>
    private static string? HashAnonymousKey(string? clientKey)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
            return null;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(clientKey));
        return Convert.ToHexString(bytes);
    }
}
