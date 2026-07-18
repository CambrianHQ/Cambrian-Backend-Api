using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Services;

public sealed class MediaStateMachine : IMediaStateMachine
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedTransitions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [TrackMediaStates.Draft] = Set(TrackMediaStates.Uploading, TrackMediaStates.Uploaded, TrackMediaStates.Failed, TrackMediaStates.Deleted),
            [TrackMediaStates.Uploading] = Set(TrackMediaStates.Uploaded, TrackMediaStates.Failed, TrackMediaStates.Deleted),
            [TrackMediaStates.Uploaded] = Set(TrackMediaStates.Processing, TrackMediaStates.Validating, TrackMediaStates.Failed, TrackMediaStates.Quarantined, TrackMediaStates.Deleted),
            [TrackMediaStates.Processing] = Set(TrackMediaStates.Validating, TrackMediaStates.Failed, TrackMediaStates.Quarantined, TrackMediaStates.Deleted),
            [TrackMediaStates.Validating] = Set(TrackMediaStates.Ready, TrackMediaStates.Failed, TrackMediaStates.Quarantined, TrackMediaStates.Deleted),
            [TrackMediaStates.Ready] = Set(TrackMediaStates.Validating, TrackMediaStates.Failed, TrackMediaStates.Quarantined, TrackMediaStates.Deleted),
            [TrackMediaStates.Failed] = Set(TrackMediaStates.Uploading, TrackMediaStates.Uploaded, TrackMediaStates.Validating, TrackMediaStates.Quarantined, TrackMediaStates.Deleted),
            [TrackMediaStates.Quarantined] = Set(TrackMediaStates.Validating, TrackMediaStates.Deleted),
            [TrackMediaStates.Deleted] = Set(),
        };

    private readonly CambrianDbContext _db;
    private readonly TimeProvider _clock;

    public MediaStateMachine(CambrianDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<TrackMedia> InitializeLegacyAsync(
        Guid trackId,
        string? legacyAudioLocation,
        CancellationToken ct = default)
    {
        var existing = await _db.TrackMedia.SingleOrDefaultAsync(x => x.TrackId == trackId, ct);
        if (existing is not null)
            return existing;

        var now = _clock.GetUtcNow().UtcDateTime;
        var recognized = TryRecognizeStableObjectKey(legacyAudioLocation, out var objectKey);
        var media = new TrackMedia
        {
            TrackId = trackId,
            ObjectKey = objectKey,
            State = recognized
                ? TrackMediaStates.Uploaded
                : string.IsNullOrWhiteSpace(legacyAudioLocation) ? TrackMediaStates.Draft : TrackMediaStates.Failed,
            FailureCode = recognized || string.IsNullOrWhiteSpace(legacyAudioLocation)
                ? null
                : "legacy_location_unrecognized",
            FailureDetail = recognized || string.IsNullOrWhiteSpace(legacyAudioLocation)
                ? null
                : "Legacy media location requires operator review before validation.",
            StateChangedAtUtc = now,
            ConcurrencyToken = Guid.NewGuid(),
        };

        _db.TrackMedia.Add(media);
        await _db.SaveChangesAsync(ct);
        return media;
    }

    public async Task<TrackMedia> TransitionAsync(
        Guid trackId,
        Guid expectedConcurrencyToken,
        string targetState,
        MediaStateMetadata metadata,
        CancellationToken ct = default)
    {
        if (!TrackMediaStates.All.Contains(targetState))
            throw new InvalidOperationException($"Unknown media state '{targetState}'.");

        var media = await _db.TrackMedia.SingleOrDefaultAsync(x => x.TrackId == trackId, ct)
            ?? throw new InvalidOperationException("Track media row does not exist.");
        if (media.ConcurrencyToken != expectedConcurrencyToken)
            throw new DbUpdateConcurrencyException("Track media was updated by another operation.");
        if (!AllowedTransitions[media.State].Contains(targetState))
            throw new InvalidOperationException($"Media state transition {media.State} -> {targetState} is not allowed.");

        media.State = targetState;
        media.StateChangedAtUtc = _clock.GetUtcNow().UtcDateTime;
        media.ConcurrencyToken = Guid.NewGuid();
        media.ObjectKey = metadata.ObjectKey ?? media.ObjectKey;
        media.FailureCode = metadata.FailureCode;
        media.FailureDetail = SafeDetail(metadata.FailureDetail);
        media.ValidatedAtUtc = metadata.ValidatedAtUtc ?? media.ValidatedAtUtc;
        media.SizeBytes = metadata.SizeBytes ?? media.SizeBytes;
        media.ContentType = metadata.ContentType ?? media.ContentType;
        media.ChecksumSha256 = metadata.ChecksumSha256 ?? media.ChecksumSha256;
        media.DurationMilliseconds = metadata.DurationMilliseconds ?? media.DurationMilliseconds;
        media.ValidationVersion = metadata.ValidationVersion ?? media.ValidationVersion;

        await _db.SaveChangesAsync(ct);
        return media;
    }

    public async Task<TrackMedia> RefreshValidationAsync(
        Guid trackId,
        Guid expectedConcurrencyToken,
        MediaStateMetadata metadata,
        CancellationToken ct = default)
    {
        var media = await _db.TrackMedia.SingleOrDefaultAsync(x => x.TrackId == trackId, ct)
            ?? throw new InvalidOperationException("Track media row does not exist.");
        if (media.ConcurrencyToken != expectedConcurrencyToken)
            throw new DbUpdateConcurrencyException("Track media was updated by another operation.");
        if (media.State != TrackMediaStates.Ready)
            throw new InvalidOperationException($"Validation refresh is only allowed from {TrackMediaStates.Ready}, not {media.State}.");

        media.ConcurrencyToken = Guid.NewGuid();
        media.ValidatedAtUtc = metadata.ValidatedAtUtc ?? media.ValidatedAtUtc;
        media.SizeBytes = metadata.SizeBytes ?? media.SizeBytes;
        media.ContentType = metadata.ContentType ?? media.ContentType;
        media.ChecksumSha256 = metadata.ChecksumSha256 ?? media.ChecksumSha256;
        media.DurationMilliseconds = metadata.DurationMilliseconds ?? media.DurationMilliseconds;
        media.ValidationVersion = metadata.ValidationVersion ?? media.ValidationVersion;

        await _db.SaveChangesAsync(ct);
        return media;
    }

    internal static bool TryRecognizeStableObjectKey(string? value, out string? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var candidate = value.Trim().Replace('\\', '/');
        if (candidate.Contains('?', StringComparison.Ordinal)
            || candidate.Contains('#', StringComparison.Ordinal)
            || Uri.TryCreate(candidate, UriKind.Absolute, out _)
            || candidate.StartsWith("/stream/", StringComparison.OrdinalIgnoreCase))
            return false;

        if (candidate.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
            candidate = candidate["/uploads/".Length..];
        else
            candidate = candidate.TrimStart('/');

        if (candidate.Length is 0 or > 1_024
            || candidate.Split('/').Any(segment => segment is "" or "." or ".."))
            return false;
        key = candidate;
        return true;
    }

    private static IReadOnlySet<string> Set(params string[] states) =>
        new HashSet<string>(states, StringComparer.Ordinal);

    private static string? SafeDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 1_000 ? sanitized : sanitized[..1_000];
    }
}
