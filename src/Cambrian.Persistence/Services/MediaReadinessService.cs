using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Services;

public sealed class MediaReadinessService : IMediaReadinessService
{
    private readonly CambrianDbContext _db;
    private readonly IMediaValidationService _validation;
    private readonly IMediaStateMachine _stateMachine;
    private readonly TimeProvider _clock;

    public MediaReadinessService(
        CambrianDbContext db,
        IMediaValidationService validation,
        IMediaStateMachine stateMachine,
        TimeProvider clock)
    {
        _db = db;
        _validation = validation;
        _stateMachine = stateMachine;
        _clock = clock;
    }

    public async Task<MediaReadinessResult> EnsureReadyAsync(Guid trackId, CancellationToken ct = default)
    {
        var media = await _db.TrackMedia
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TrackId == trackId, ct);
        if (media is null || string.IsNullOrWhiteSpace(media.ObjectKey))
            return Failure("track_not_ready", "Track media is not ready.", media?.State);

        if (media.State == TrackMediaStates.Ready)
            return new MediaReadinessResult(true, MediaState: media.State);

        if (media.State == TrackMediaStates.Validating)
            return Failure("media_validating", "Track media validation is already in progress. Try again shortly.", media.State);

        if (media.State is not (TrackMediaStates.Uploaded or TrackMediaStates.Failed))
            return Failure("track_not_ready", "Track media is not ready.", media.State);

        // Promote-on-publish: Uploaded/Failed -> Validating -> Ready|Failed|Quarantined
        // are all allowed by MediaStateMachine. Validating -> Uploaded is not, so a
        // storage outage mid-validation parks the row in Failed — a retryable state
        // (Failed -> Validating is allowed) — rather than deadlocking publication.
        var originalState = media.State;
        var originalFailureCode = media.FailureCode;
        var originalFailureDetail = media.FailureDetail;
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
            // Lost the race — another request (playback refresh or reconciliation)
            // holds the row. The creator retries once that pass settles.
            return Failure("media_validating", "Track media validation is already in progress. Try again shortly.", TrackMediaStates.Validating);
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
                // Validation tooling being down is not evidence against the media.
                // Restore the original failure metadata for a Failed-origin row;
                // an Uploaded-origin row records the outage as its failure code.
                media = await _stateMachine.TransitionAsync(
                    trackId,
                    media.ConcurrencyToken,
                    TrackMediaStates.Failed,
                    originalState == TrackMediaStates.Failed
                        ? new MediaStateMetadata(FailureCode: originalFailureCode, FailureDetail: originalFailureDetail)
                        : new MediaStateMetadata(
                            FailureCode: "storage_unavailable",
                            FailureDetail: "Storage was unavailable during publish validation."),
                    ct);
                return Failure("storage_unavailable", "Playback storage is temporarily unavailable.", media.State);
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
                ? Failure("media_object_missing", "Track media is temporarily unavailable.", targetState)
                : Failure(result.FailureCode ?? "media_validation_failed", "Track media did not pass validation.", targetState);
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
        return new MediaReadinessResult(true, MediaState: media.State);
    }

    private static MediaReadinessResult Failure(string code, string message, string? mediaState = null) =>
        new(false, code, message, mediaState);
}
