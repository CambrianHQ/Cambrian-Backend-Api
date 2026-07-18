using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Observability;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cambrian.Persistence.Services;

/// <summary>
/// PostgreSQL-authoritative qualified-play acceptance. Starts create pending playback
/// sessions; only StopAsync can append a qualified event and increment TrackStats.
/// </summary>
public sealed class PlaybackTrackingService : IPlaybackTrackingService
{
    private static readonly string[] BotUserAgentTokens =
    {
        "bot", "crawler", "spider", "slurp", "headlesschrome", "facebookexternalhit",
        "discordbot", "twitterbot", "bytespider", "semrush", "ahrefs"
    };

    private readonly CambrianDbContext _db;
    private readonly ITrackVisibilityPolicy _visibility;
    private readonly IPlaybackAnalyticsService _analytics;
    private readonly TimeProvider _clock;
    private readonly PlaybackOptions _options;
    private readonly byte[] _listenerHashSecret;
    private readonly ILogger<PlaybackTrackingService> _logger;

    public PlaybackTrackingService(
        CambrianDbContext db,
        ITrackVisibilityPolicy visibility,
        IPlaybackAnalyticsService analytics,
        TimeProvider clock,
        IOptions<PlaybackOptions> options,
        IConfiguration configuration,
        ILogger<PlaybackTrackingService> logger)
    {
        _db = db;
        _visibility = visibility;
        _analytics = analytics;
        _clock = clock;
        _options = options.Value;
        _logger = logger;

        var secret = _options.ListenerHashSecret ?? configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Playback:ListenerHashSecret or Jwt:Key must be configured.");
        _listenerHashSecret = Encoding.UTF8.GetBytes(secret);
    }

    public async Task<PlaybackStartResult> StartAsync(PlaybackStartCommand command, CancellationToken ct = default)
    {
        var now = UtcNow();
        var track = await _db.Tracks.SingleOrDefaultAsync(t => t.Id == command.TrackId, ct);
        if (track is null || !_visibility.CanAccess(track.Visibility, track.CreatorId, command.UserId, command.IsAdmin))
            throw Error("track_not_found", "Track not found.", 404);

        var threshold = CalculateThresholdSeconds(track.Duration);
        if (IsBot(command.UserAgent))
        {
            CambrianMetrics.QualifiedPlayRejected.Add(1, new KeyValuePair<string, object?>("reason", "bot"));
            _logger.LogInformation("EVENT: playback_start_rejected trackId:{TrackId} reason:bot", track.Id);
            return new(null, "bot", threshold, DedupeMinutes, now, false);
        }

        var listener = ResolveListener(command.UserId, command.AnonymousSessionId);
        var startIdempotencyKey = NormalizeRequestKey("start", listener.KeyHash, command.IdempotencyKey);

        await using var transaction = await BeginTransactionAsync(ct);
        if (startIdempotencyKey is not null)
            await AcquireAdvisoryLockAsync($"qualified-play-idem:{startIdempotencyKey}", ct);
        await AcquireAdvisoryLockAsync($"qualified-play-track:{track.Id:D}", ct);
        await _db.Entry(track).ReloadAsync(ct);

        if (startIdempotencyKey is not null)
        {
            var replay = await _db.StreamSessions
                .AsNoTracking()
                .SingleOrDefaultAsync(s => s.IdempotencyKey == startIdempotencyKey, ct);
            if (replay is not null)
            {
                await CommitAsync(transaction, ct);
                return ToStartReplay(replay, now, listener.IsAnonymous);
            }
        }

        var cutoff = now.AddMinutes(-DedupeMinutes);
        var recent = await _db.StreamSessions
            .Where(s => s.TrackId == track.Id
                        && s.ListenerKeyHash == listener.KeyHash
                        && s.StartedAt >= cutoff)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (recent is not null)
        {
            if (recent.QualificationStatus == "pending" && recent.StoppedAt is not null)
            {
                recent.StoppedAt = null;
                recent.LastStartedAtUtc = now;
                await _db.SaveChangesAsync(ct);
                await CommitAsync(transaction, ct);
                return new(recent.Id, "resumed", recent.QualificationThresholdSeconds,
                    DedupeMinutes, now, listener.IsAnonymous);
            }

            if (recent.StoppedAt is null || recent.QualificationStatus is "qualified" or "deduplicated")
            {
                await CommitAsync(transaction, ct);
                var status = recent.StoppedAt is null ? "already_started" : "already_counted";
                return new(recent.Id, status, recent.QualificationThresholdSeconds,
                    DedupeMinutes, now, listener.IsAnonymous);
            }
        }

        var ownerPreview = !string.IsNullOrEmpty(command.UserId)
            && string.Equals(command.UserId, track.CreatorId, StringComparison.Ordinal);
        var eligible = IsEligible(track);
        var session = new StreamSession
        {
            Id = Guid.NewGuid(),
            TrackId = track.Id,
            UserId = command.UserId,
            Title = track.Title,
            StartedAt = now,
            LastStartedAtUtc = now,
            ListenerKeyHash = listener.KeyHash,
            AnonymousSessionHash = listener.AnonymousHash,
            IdempotencyKey = startIdempotencyKey,
            IsOwnerPreview = ownerPreview,
            WasEligibleAtStart = eligible,
            QualificationThresholdSeconds = threshold,
            ActivePlaybackSeconds = 0,
            QualificationStatus = ownerPreview ? "owner_preview" : eligible ? "pending" : "ineligible"
        };

        _db.StreamSessions.Add(session);
        await _db.SaveChangesAsync(ct);
        await CommitAsync(transaction, ct);

        _logger.LogInformation(
            "EVENT: playback_session_started sessionId:{SessionId} trackId:{TrackId} status:{Status} thresholdSeconds:{Threshold}",
            session.Id, session.TrackId, session.QualificationStatus, threshold);

        var startStatus = session.QualificationStatus == "pending"
            ? "started"
            : session.QualificationStatus;
        return new(session.Id, startStatus, threshold, DedupeMinutes, now, listener.IsAnonymous);
    }

    public async Task<PlaybackStopResult> StopAsync(PlaybackStopCommand command, CancellationToken ct = default)
    {
        ValidateStopEvidence(command);
        var now = UtcNow();
        if (IsBot(command.UserAgent))
            throw Error("playback_session_not_found", "Playback session not found.", 404);

        var listener = ResolveListener(command.UserId, command.AnonymousSessionId);
        var sessionLocator = await _db.StreamSessions.AsNoTracking()
            .Where(s => s.Id == command.PlaybackSessionId)
            .Select(s => new { s.TrackId })
            .SingleOrDefaultAsync(ct);
        if (sessionLocator is null)
            throw Error("playback_session_not_found", "Playback session not found.", 404);

        var stopIdempotencyKey = NormalizeRequestKey("stop", listener.KeyHash, command.IdempotencyKey)
            ?? Hash($"stop:{command.PlaybackSessionId:D}");

        await using var transaction = await BeginTransactionAsync(ct);
        await AcquireAdvisoryLockAsync($"qualified-play-idem:{stopIdempotencyKey}", ct);
        await AcquireAdvisoryLockAsync($"qualified-play-track:{sessionLocator.TrackId:D}", ct);

        var session = await _db.StreamSessions
            .Include(s => s.Track)
            .SingleOrDefaultAsync(s => s.Id == command.PlaybackSessionId, ct);
        if (session is null || !OwnsSession(session, command.UserId, command.IsAdmin, listener.KeyHash))
            throw Error("playback_session_not_found", "Playback session not found.", 404);

        var existingBySession = await _db.QualifiedPlayEvents.AsNoTracking()
            .SingleOrDefaultAsync(e => e.PlaybackSessionId == session.Id, ct);
        if (existingBySession is not null)
        {
            var count = await LifetimeCountAsync(session.TrackId, ct);
            await CommitAsync(transaction, ct);
            CambrianMetrics.QualifiedPlayDuplicate.Add(1,
                new KeyValuePair<string, object?>("reason", "idempotent_replay"));
            return StopResult(session, "qualified", true, false, true,
                existingBySession.QualifiedAtUtc, count, now);
        }

        var existingByKey = await _db.QualifiedPlayEvents.AsNoTracking()
            .SingleOrDefaultAsync(e => e.IdempotencyKey == stopIdempotencyKey, ct);
        if (existingByKey is not null)
        {
            if (existingByKey.PlaybackSessionId != session.Id)
                throw Error("idempotency_key_reused", "The idempotency key was already used for another playback session.", 409);

            var count = await LifetimeCountAsync(session.TrackId, ct);
            await CommitAsync(transaction, ct);
            return StopResult(session, "qualified", true, false, true,
                existingByKey.QualifiedAtUtc, count, now);
        }

        // Historical sessions are preserved as an explicit legacy baseline. The old rows
        // contain no active-time evidence and must never be fabricated into ledger events.
        if (session.QualificationStatus == "legacy_unqualified")
        {
            session.StoppedAt ??= now;
            await _db.SaveChangesAsync(ct);
            await CommitAsync(transaction, ct);
            return StopResult(session, "legacy_unqualified", false, false, false, null, null, now);
        }

        // A stop closes one playback segment. Retrying it (even under a different
        // request key) must not add the same client evidence again. The client must
        // explicitly resume through /stream/start before another segment can count.
        if (session.StoppedAt is not null && session.LastStartedAtUtc is null)
        {
            var thresholdMet = session.QualificationStatus == "deduplicated";
            var count = thresholdMet ? await LifetimeCountAsync(session.TrackId, ct) : null;
            await CommitAsync(transaction, ct);
            CambrianMetrics.QualifiedPlayDuplicate.Add(1,
                new KeyValuePair<string, object?>("reason", "stopped_segment_replay"));
            return StopResult(session, session.QualificationStatus, thresholdMet, false, true,
                session.QualifiedAtUtc, count, now);
        }

        var segmentStartedAt = session.LastStartedAtUtc ?? session.StartedAt;
        var serverElapsed = Math.Clamp((now - segmentStartedAt).TotalSeconds, 0, MaximumSegmentSeconds);
        var segmentActive = CalculateActiveSegment(command, serverElapsed);
        session.ActivePlaybackSeconds = Math.Min(
            MaximumSegmentSeconds,
            Math.Max(0, session.ActivePlaybackSeconds) + segmentActive);
        session.StoppedAt = now;
        session.LastStartedAtUtc = null;

        if (session.IsOwnerPreview)
            return await RejectAsync(transaction, session, "owner_preview", now, ct);

        if (!session.WasEligibleAtStart || !IsEligible(session.Track))
            return await RejectAsync(transaction, session, "ineligible", now, ct);

        if (session.ActivePlaybackSeconds + 0.001 < session.QualificationThresholdSeconds)
        {
            session.QualificationStatus = "pending";
            await _db.SaveChangesAsync(ct);
            await CommitAsync(transaction, ct);
            return StopResult(session, "pending", false, false, false, null, null, now);
        }

        var dedupeCutoff = now.AddMinutes(-DedupeMinutes);
        var priorAccepted = await _db.QualifiedPlayEvents.AsNoTracking()
            .Where(e => e.TrackId == session.TrackId
                        && e.ListenerKeyHash == listener.KeyHash
                        && e.QualifiedAtUtc >= dedupeCutoff)
            .OrderByDescending(e => e.QualifiedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (priorAccepted is not null)
        {
            session.QualificationStatus = "deduplicated";
            await _db.SaveChangesAsync(ct);
            var count = await LifetimeCountAsync(session.TrackId, ct);
            await CommitAsync(transaction, ct);
            CambrianMetrics.QualifiedPlayDuplicate.Add(1,
                new KeyValuePair<string, object?>("reason", "listener_window"));
            return StopResult(session, "deduplicated", true, false, false,
                priorAccepted.QualifiedAtUtc, count, now);
        }

        var playEvent = new QualifiedPlayEvent
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = stopIdempotencyKey,
            TrackId = session.TrackId,
            CreatorId = session.Track.CreatorId,
            ListenerUserId = command.UserId,
            ListenerKeyHash = listener.KeyHash,
            AnonymousSessionHash = listener.AnonymousHash,
            PlaybackSessionId = session.Id,
            QualifiedAtUtc = now,
            QualificationBasis = "active_playback_threshold",
            ActivePlaybackSeconds = session.ActivePlaybackSeconds,
            ThresholdSeconds = session.QualificationThresholdSeconds,
            CreatedAtUtc = now,
            AggregatedAtUtc = now
        };

        session.QualificationStatus = "qualified";
        session.QualifiedAtUtc = now;
        _db.QualifiedPlayEvents.Add(playEvent);

        var stats = await _db.TrackStats.SingleOrDefaultAsync(s => s.TrackId == session.TrackId, ct);
        if (stats is null)
        {
            stats = new TrackStat
            {
                TrackId = session.TrackId,
                LegacyPlayCount = 0,
                QualifiedPlayCount = 1,
                PlayCount = 1,
                LastPlayedAt = now,
                UpdatedAt = now,
                ReconciledAtUtc = now
            };
            _db.TrackStats.Add(stats);
        }
        else
        {
            stats.QualifiedPlayCount++;
            stats.PlayCount = checked(stats.LegacyPlayCount + stats.QualifiedPlayCount);
            stats.LastPlayedAt = now;
            stats.UpdatedAt = now;
            stats.ReconciledAtUtc = now;
        }

        await _db.SaveChangesAsync(ct);
        await CommitAsync(transaction, ct);

        CambrianMetrics.QualifiedPlayAccepted.Add(1);
        _logger.LogInformation(
            "EVENT: qualified_play_accepted eventId:{EventId} sessionId:{SessionId} trackId:{TrackId} creatorId:{CreatorId} qualifiedAtUtc:{QualifiedAtUtc}",
            playEvent.Id, session.Id, session.TrackId, playEvent.CreatorId, now);

        try
        {
            await _analytics.CaptureAcceptedAsync(new PlaybackAnalyticsEvent(
                playEvent.Id,
                playEvent.TrackId,
                playEvent.CreatorId,
                playEvent.ListenerKeyHash,
                playEvent.QualifiedAtUtc,
                playEvent.ActivePlaybackSeconds,
                playEvent.ThresholdSeconds,
                listener.IsAnonymous), CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The PostgreSQL commit above is final. A telemetry outage must never turn a
            // durably accepted play into a failed API response or trigger client retries.
            _logger.LogWarning(ex,
                "Qualified-play analytics mirror failed open for eventId:{EventId}",
                playEvent.Id);
        }

        return StopResult(session, "qualified", true, true, false, now, stats.PlayCount, now);
    }

    private async Task<PlaybackStopResult> RejectAsync(
        IDbContextTransaction? transaction,
        StreamSession session,
        string reason,
        DateTime now,
        CancellationToken ct)
    {
        session.QualificationStatus = reason;
        await _db.SaveChangesAsync(ct);
        await CommitAsync(transaction, ct);
        CambrianMetrics.QualifiedPlayRejected.Add(1,
            new KeyValuePair<string, object?>("reason", reason));
        return StopResult(session, reason, false, false, false, null, null, now);
    }

    private double CalculateActiveSegment(PlaybackStopCommand command, double serverElapsed)
    {
        var pauseAdjustedElapsed = command.PausedSeconds is { } paused
            ? Math.Clamp(serverElapsed - paused, 0, MaximumSegmentSeconds)
            : serverElapsed;

        if (command.ActivePlaybackSeconds is { } reported)
            return Math.Clamp(Math.Min(reported, pauseAdjustedElapsed), 0, MaximumSegmentSeconds);

        if (command.PausedSeconds.HasValue)
            return pauseAdjustedElapsed;

        // Without active-time evidence, ending position and seek position cannot prove
        // playback. The session remains pending until a later, evidence-bearing stop.
        return 0;
    }

    private void ValidateStopEvidence(PlaybackStopCommand command)
    {
        if (command.ActivePlaybackSeconds is < 0 || command.ActivePlaybackSeconds > MaximumSegmentSeconds)
            throw Error("invalid_active_playback_seconds",
                $"activePlaybackSeconds must be between 0 and {MaximumSegmentSeconds}.", 400);
        if (command.PausedSeconds is < 0 || command.PausedSeconds > MaximumSegmentSeconds)
            throw Error("invalid_paused_seconds",
                $"pausedSeconds must be between 0 and {MaximumSegmentSeconds}.", 400);
        if (command.SeekCount is < 0 or > 100_000)
            throw Error("invalid_seek_count", "seekCount must be between 0 and 100000.", 400);
        if (command.EndingPositionSeconds is < 0 || command.EndingPositionSeconds > MaximumSegmentSeconds)
            throw Error("invalid_ending_position_seconds",
                $"endingPositionSeconds must be between 0 and {MaximumSegmentSeconds}.", 400);
    }

    private double CalculateThresholdSeconds(string? duration)
    {
        var ceiling = Math.Max(1, _options.QualificationSeconds);
        var fraction = Math.Clamp(_options.QualificationTrackFraction, 0.01, 1);
        var durationSeconds = ParseDurationSeconds(duration);
        return durationSeconds is > 0
            ? Math.Max(0.1, Math.Min(ceiling, durationSeconds.Value * fraction))
            : ceiling;
    }

    internal static double? ParseDurationSeconds(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration)) return null;
        if (double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
            return seconds;

        var parts = duration.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var remainingSeconds))
            return minutes * 60 + remainingSeconds;

        if (parts.Length == 3
            && double.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            && double.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out remainingSeconds))
            return hours * 3600 + minutes * 60 + remainingSeconds;

        return null;
    }

    private ListenerIdentity ResolveListener(string? userId, string? anonymousSessionId)
    {
        if (!string.IsNullOrWhiteSpace(userId))
            return new(Hash($"user:{userId}"), null, false);

        if (string.IsNullOrWhiteSpace(anonymousSessionId))
            throw Error("anonymous_session_required",
                "Anonymous playback requires X-Cambrian-Anonymous-Session or the Cambrian playback cookie.", 400);
        if (anonymousSessionId.Length > 256)
            throw Error("invalid_anonymous_session",
                "X-Cambrian-Anonymous-Session must be 256 characters or fewer.", 400);

        var anonymousHash = Hmac($"anonymous:{anonymousSessionId.Trim()}");
        return new(anonymousHash, anonymousHash, true);
    }

    private static bool OwnsSession(StreamSession session, string? userId, bool isAdmin, string listenerHash)
    {
        if (isAdmin) return true;
        if (!string.IsNullOrWhiteSpace(userId))
            return string.Equals(session.UserId, userId, StringComparison.Ordinal);
        return session.UserId is null
            && session.ListenerKeyHash is not null
            && FixedTimeEquals(session.ListenerKeyHash, listenerHash);
    }

    private static bool IsEligible(Track track) =>
        string.Equals(track.Visibility, "public", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(track.Status, "available", StringComparison.OrdinalIgnoreCase)
            || string.Equals(track.Status, "active", StringComparison.OrdinalIgnoreCase))
        && track.DeletedAt is null
        && track.PurgeRequestedAt is null
        && track.PurgedAt is null
        && !track.ExclusiveSold
        && !string.IsNullOrWhiteSpace(track.AudioUrl);

    private static bool IsBot(string? userAgent) =>
        !string.IsNullOrWhiteSpace(userAgent)
        && BotUserAgentTokens.Any(token => userAgent.Contains(token, StringComparison.OrdinalIgnoreCase));

    private PlaybackStartResult ToStartReplay(StreamSession session, DateTime now, bool isAnonymous) =>
        new(session.Id,
            session.StoppedAt is null ? "already_started" : "already_counted",
            session.QualificationThresholdSeconds,
            DedupeMinutes,
            now,
            isAnonymous);

    private static PlaybackStopResult StopResult(
        StreamSession session,
        string status,
        bool qualified,
        bool counted,
        bool replay,
        DateTime? qualifiedAt,
        long? lifetime,
        DateTime now) =>
        new(session.Id, status, qualified, counted, replay, session.ActivePlaybackSeconds,
            session.QualificationThresholdSeconds, qualifiedAt, lifetime, now);

    private async Task<long?> LifetimeCountAsync(Guid trackId, CancellationToken ct) =>
        await _db.TrackStats.Where(s => s.TrackId == trackId)
            .Select(s => (long?)s.PlayCount)
            .SingleOrDefaultAsync(ct);

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken ct) =>
        _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;

    private static Task CommitAsync(IDbContextTransaction? transaction, CancellationToken ct) =>
        transaction is null ? Task.CompletedTask : transaction.CommitAsync(ct);

    private async Task AcquireAdvisoryLockAsync(string key, CancellationToken ct)
    {
        if (!IsPostgres) return;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }

    private string? NormalizeRequestKey(string operation, string listenerHash, string? requestKey)
    {
        if (string.IsNullOrWhiteSpace(requestKey)) return null;
        if (requestKey.Length > 128)
            throw Error("invalid_idempotency_key", "Idempotency-Key must be 128 characters or fewer.", 400);
        return Hash($"{operation}:{listenerHash}:{requestKey.Trim()}");
    }

    private string Hmac(string value)
    {
        using var hmac = new HMACSHA256(_listenerHashSecret);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private static PlaybackTrackingException Error(string code, string message, int statusCode) =>
        new(code, message, statusCode);

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;
    private int DedupeMinutes => Math.Clamp(_options.DeduplicationWindowMinutes, 1, 43_200);
    private double MaximumSegmentSeconds => Math.Clamp(_options.MaximumSegmentSeconds, 1, 86_400);
    private bool IsPostgres => _db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    private sealed record ListenerIdentity(string KeyHash, string? AnonymousHash, bool IsAnonymous);
}
