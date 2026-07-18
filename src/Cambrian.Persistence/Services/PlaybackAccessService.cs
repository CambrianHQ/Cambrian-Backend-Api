using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cambrian.Persistence.Services;

public sealed class PlaybackAccessService : IPlaybackAccessService
{
    private readonly CambrianDbContext _db;
    private readonly ITrackVisibilityPolicy _visibility;
    private readonly IMediaValidationService _validation;
    private readonly IMediaStateMachine _stateMachine;
    private readonly PlaybackMediaOptions _options;
    private readonly TimeProvider _clock;

    public PlaybackAccessService(
        CambrianDbContext db,
        ITrackVisibilityPolicy visibility,
        IMediaValidationService validation,
        IMediaStateMachine stateMachine,
        IOptions<PlaybackMediaOptions> options,
        TimeProvider clock)
    {
        _db = db;
        _visibility = visibility;
        _validation = validation;
        _stateMachine = stateMachine;
        _options = options.Value;
        _clock = clock;
    }

    public async Task<PlaybackAccessResult> PrepareAsync(
        Guid trackId,
        string? listenerUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var track = await _db.Tracks
            .AsNoTracking()
            .Include(x => x.Media)
            .SingleOrDefaultAsync(x => x.Id == trackId, ct);
        if (track is null)
            return Failure(PlaybackAccessOutcome.NotFound, trackId, "track_not_found", "Track not found.");

        if (!_visibility.CanAccess(track.Visibility, track.CreatorId, listenerUserId, isAdmin))
        {
            // Mask inaccessible tracks as 404 for every caller — the platform-wide
            // TrackVisibilityPolicy contract — so hidden track IDs cannot be enumerated.
            return Failure(PlaybackAccessOutcome.NotFound, trackId, "track_not_found", "Track not found.");
        }

        var media = track.Media;
        if (media is null
            || string.Equals(track.Status, "removed", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(media.ObjectKey))
            return Failure(PlaybackAccessOutcome.NotReady, trackId, "track_not_ready", "Track media is not ready.", media?.State);

        // A row parked in Validating that has passed validation before was Ready
        // moments ago — another request is refreshing it. Keep serving rather than
        // failing the listener for the duration of a background recheck.
        if (media.State == TrackMediaStates.Validating && media.ValidatedAtUtc.HasValue)
            return Playable(track, media, listenerUserId);

        if (media.State != TrackMediaStates.Ready)
            return Failure(PlaybackAccessOutcome.NotReady, trackId, "track_not_ready", "Track media is not ready.", media.State);

        var staleBefore = _clock.GetUtcNow().UtcDateTime.AddMinutes(-_options.ValidationMaxAgeMinutes);
        if (!media.ValidatedAtUtc.HasValue || media.ValidatedAtUtc < staleBefore)
        {
            var previouslyValidated = media.ValidatedAtUtc.HasValue;
            try
            {
                media = await _stateMachine.TransitionAsync(
                    trackId,
                    media.ConcurrencyToken,
                    TrackMediaStates.Validating,
                    new MediaStateMetadata(),
                    ct);
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException or InvalidOperationException)
            {
                // Lost the revalidation race — another request holds the row. A
                // previously validated track stays playable on its last-known-good
                // metadata; a never-validated one is not ready yet.
                return previouslyValidated
                    ? Playable(track, media, listenerUserId)
                    : Failure(PlaybackAccessOutcome.NotReady, trackId, "track_not_ready", "Track media is not ready.", media.State);
            }

            var result = await _validation.ValidateAsync(new MediaValidationRequest(
                trackId,
                media.ObjectKey!,
                media.SizeBytes,
                media.ContentType,
                media.ChecksumSha256), ct);
            if (!result.IsValid)
            {
                if (result.DependencyUnavailable)
                {
                    media = await _stateMachine.TransitionAsync(
                        trackId,
                        media.ConcurrencyToken,
                        TrackMediaStates.Ready,
                        new MediaStateMetadata(),
                        ct);
                    // Validation tooling being down is not evidence against media that
                    // already passed validation — keep serving last-known-good.
                    return previouslyValidated
                        ? Playable(track, media, listenerUserId)
                        : Failure(PlaybackAccessOutcome.StorageUnavailable, trackId, "storage_unavailable", "Playback storage is temporarily unavailable.", media.State);
                }

                var targetState = result.FailureCode is "checksum_mismatch" or "media_parse_failed" or "decode_probe_failed"
                    ? TrackMediaStates.Quarantined
                    : TrackMediaStates.Failed;
                await _stateMachine.TransitionAsync(
                    trackId,
                    media.ConcurrencyToken,
                    targetState,
                    new MediaStateMetadata(
                        FailureCode: result.FailureCode,
                        FailureDetail: result.SafeDetail,
                        SizeBytes: result.SizeBytes,
                        ContentType: result.ContentType,
                        ValidationVersion: result.ValidationVersion),
                    ct);
                return result.FailureCode == "media_object_missing"
                    ? Failure(PlaybackAccessOutcome.ObjectMissing, trackId, "media_object_missing", "Track media is temporarily unavailable.", targetState)
                    : Failure(PlaybackAccessOutcome.ValidationFailed, trackId, "media_validation_failed", "Track media did not pass validation.", targetState);
            }

            media = await _stateMachine.TransitionAsync(
                trackId,
                media.ConcurrencyToken,
                TrackMediaStates.Ready,
                new MediaStateMetadata(
                    ValidatedAtUtc: _clock.GetUtcNow().UtcDateTime,
                    SizeBytes: result.SizeBytes,
                    ContentType: result.ContentType,
                    ChecksumSha256: result.ChecksumSha256,
                    DurationMilliseconds: result.DurationMilliseconds,
                    ValidationVersion: result.ValidationVersion),
                ct);
        }

        return Playable(track, media, listenerUserId);
    }

    private static PlaybackAccessResult Playable(Track track, TrackMedia media, string? listenerUserId) =>
        new(
            PlaybackAccessOutcome.Ready,
            track.Id,
            media.State,
            media.ContentType,
            media.SizeBytes,
            string.Equals(track.Visibility, "public", StringComparison.OrdinalIgnoreCase) ? null : listenerUserId,
            ObjectKey: media.ObjectKey);

    public async Task<PlaybackAccessResult> PrepareTicketStreamAsync(
        Guid trackId,
        bool allowValidating,
        CancellationToken ct = default)
    {
        var target = await _db.Tracks
            .AsNoTracking()
            .Where(x => x.Id == trackId)
            .Select(x => new
            {
                x.Status,
                MediaState = x.Media == null ? null : x.Media.State,
                ObjectKey = x.Media == null ? null : x.Media.ObjectKey,
                ContentType = x.Media == null ? null : x.Media.ContentType,
                SizeBytes = x.Media == null ? null : x.Media.SizeBytes,
                ValidatedAtUtc = x.Media == null ? null : x.Media.ValidatedAtUtc,
            })
            .SingleOrDefaultAsync(ct);
        if (target is null)
            return Failure(PlaybackAccessOutcome.NotFound, trackId, "track_not_found", "Track not found.");
        // A previously validated row parked in Validating is mid-recheck, not broken —
        // ticket holders keep streaming through the revalidation window.
        var streamableState = target.MediaState == TrackMediaStates.Ready
            || (target.MediaState == TrackMediaStates.Validating
                && (allowValidating || target.ValidatedAtUtc.HasValue));
        if (string.Equals(target.Status, "removed", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(target.ObjectKey)
            || !streamableState)
            return Failure(PlaybackAccessOutcome.NotReady, trackId, "track_not_ready", "Track media is not ready.", target.MediaState);
        return new PlaybackAccessResult(
            PlaybackAccessOutcome.Ready,
            trackId,
            target.MediaState,
            target.ContentType,
            target.SizeBytes,
            ObjectKey: target.ObjectKey);
    }

    public Task<bool> IsMediaReadyAsync(Guid trackId, CancellationToken ct = default) =>
        _db.TrackMedia.AsNoTracking().AnyAsync(
            x => x.TrackId == trackId
                && x.State == TrackMediaStates.Ready
                && x.ObjectKey != null
                && x.ObjectKey != "",
            ct);

    private static PlaybackAccessResult Failure(
        PlaybackAccessOutcome outcome,
        Guid trackId,
        string code,
        string message,
        string? mediaState = null) =>
        new(outcome, trackId, mediaState, ErrorCode: code, SafeMessage: message);
}
