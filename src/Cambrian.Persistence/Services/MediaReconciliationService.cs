using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cambrian.Persistence.Services;

public sealed class MediaReconciliationService : IMediaReconciliationService
{
    // Audio objects live under this prefix; covers/invoices in other prefixes must
    // not be classified as playback orphans. Legacy keys outside the prefix are
    // still resolved per track through GetMetadataAsync.
    private const string AudioObjectPrefix = "tracks/";

    private static readonly IReadOnlySet<string> SupportedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "audio/mpeg", "audio/mp3", "audio/wav", "audio/x-wav", "audio/flac",
        "audio/aac", "audio/ogg", "audio/mp4", "audio/x-m4a",
    };

    private readonly CambrianDbContext _db;
    private readonly IObjectStorage _storage;
    private readonly IMediaValidationService _validation;
    private readonly IMediaStateMachine _stateMachine;
    private readonly TimeProvider _clock;
    private readonly ILogger<MediaReconciliationService> _logger;

    public MediaReconciliationService(
        CambrianDbContext db,
        IObjectStorage storage,
        IMediaValidationService validation,
        IMediaStateMachine stateMachine,
        TimeProvider clock,
        ILogger<MediaReconciliationService> logger)
    {
        _db = db;
        _storage = storage;
        _validation = validation;
        _stateMachine = stateMachine;
        _clock = clock;
        _logger = logger;
    }

    public async Task<MediaReconciliationSummary> RunAsync(bool remediate, CancellationToken ct = default)
    {
        var created = await CreateRunAsync(remediate, ct);
        return await ExecuteRunAsync(created.RunId, ct);
    }

    public async Task<MediaReconciliationSummary> CreateRunAsync(bool remediate, CancellationToken ct = default)
    {
        var run = new MediaReconciliationRun
        {
            Id = Guid.NewGuid(),
            StartedAtUtc = _clock.GetUtcNow().UtcDateTime,
            RemediationEnabled = remediate,
        };
        _db.MediaReconciliationRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        return ToSummary(run);
    }

    public async Task<MediaReconciliationSummary> ExecuteRunAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _db.MediaReconciliationRuns.SingleOrDefaultAsync(x => x.Id == runId, ct)
            ?? throw new InvalidOperationException("Reconciliation run does not exist.");

        var publishedFailures = new HashSet<Guid>();
        try
        {
            await ScanAsync(run, publishedFailures, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Status = "cancelled";
            run.FailureCode = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            run.Status = "failed";
            run.FailureCode = ex is HttpRequestException or TimeoutException or TaskCanceledException
                ? "storage_unavailable"
                : "reconciliation_failed";
            _logger.LogError(ex, "Media reconciliation run {RunId} failed with {FailureCode}", run.Id, run.FailureCode);
        }
        finally
        {
            run.CompletedAtUtc = _clock.GetUtcNow().UtcDateTime;
            run.FindingCount = run.Findings.Count;
            run.UnresolvedPublishedTrackFailures = publishedFailures.Count;
            await _db.SaveChangesAsync(CancellationToken.None);
        }

        return ToSummary(run);
    }

    private async Task ScanAsync(MediaReconciliationRun run, ISet<Guid> publishedFailures, CancellationToken ct)
    {
        var tracks = await _db.Tracks.AsNoTracking().Include(x => x.Media).ToListAsync(ct);
        run.TracksInspected = tracks.Count;

        var objects = new Dictionary<string, StorageObjectMetadata>(StringComparer.Ordinal);
        await foreach (var item in _storage.ListAsync(AudioObjectPrefix, ct))
        {
            objects[item.Key] = item;
            run.ObjectsInspected++;
        }

        if (objects.Count == 0 && tracks.Any(x => !string.IsNullOrWhiteSpace(x.Media?.ObjectKey)))
        {
            run.Status = "failed";
            run.FailureCode = "storage_listing_empty";
            _logger.LogError(
                "Media reconciliation run {RunId} aborted: storage listing returned zero objects while the database has known keys",
                run.Id);
            return;
        }

        var knownKeys = new HashSet<string>(StringComparer.Ordinal);
        var mediaKeys = new List<(Guid TrackId, string ObjectKey)>();
        foreach (var track in tracks)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await InspectTrackAsync(run, track, objects, knownKeys, mediaKeys, publishedFailures, ct);
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException or InvalidOperationException)
            {
                // Detach conflicted entries so the shared context can keep
                // saving findings and later tracks after a lost race.
                if (ex is DbUpdateConcurrencyException concurrency)
                    foreach (var entry in concurrency.Entries)
                        entry.State = EntityState.Detached;
                AddFinding(run, track.Id, "concurrent_modification", "warning", null,
                    "Another operation modified the media row while reconciliation was inspecting it.", "none");
                _logger.LogWarning(ex,
                    "Media reconciliation run {RunId} skipped track {TrackId} after a concurrent modification",
                    run.Id, track.Id);
            }
        }

        foreach (var duplicate in mediaKeys
            .GroupBy(x => x.ObjectKey, StringComparer.Ordinal)
            .Where(x => x.Count() > 1))
        {
            foreach (var (trackId, _) in duplicate)
                AddFinding(run, trackId, "duplicate_object_key", "error", duplicate.Key,
                    "Multiple database rows reference the same object key.");
        }

        foreach (var orphan in objects.Keys.Where(key => !knownKeys.Contains(key)))
            AddFinding(run, null, "object_without_database_row", "warning", orphan,
                "A storage object has no matching TrackMedia row.");

        run.Status = "completed";
    }

    private async Task InspectTrackAsync(
        MediaReconciliationRun run,
        Track track,
        IReadOnlyDictionary<string, StorageObjectMetadata> objects,
        ISet<string> knownKeys,
        ICollection<(Guid TrackId, string ObjectKey)> mediaKeys,
        ISet<Guid> publishedFailures,
        CancellationToken ct)
    {
        var remediate = run.RemediationEnabled;
        var media = track.Media ?? await _stateMachine.InitializeLegacyAsync(track.Id, track.AudioUrl, ct);
        var isPublished = string.Equals(track.Visibility, "public", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(track.Status, "removed", StringComparison.OrdinalIgnoreCase);

        if (LooksLikeLegacyLocation(track.AudioUrl))
            AddFinding(run, track.Id, "legacy_bucket_domain_reference", "warning", media.ObjectKey,
                "A legacy URL or proxy location requires deterministic operator mapping.");

        if (isPublished && media.State != TrackMediaStates.Ready)
        {
            AddFinding(run, track.Id, "published_track_not_ready", "error", media.ObjectKey,
                "A published track is not in Ready media state.");
            publishedFailures.Add(track.Id);
        }

        if (string.IsNullOrWhiteSpace(media.ObjectKey))
        {
            AddFinding(run, track.Id, "database_row_without_object", "error", null,
                "The media row does not contain a trustworthy object key.");
            if (isPublished) publishedFailures.Add(track.Id);
            return;
        }

        knownKeys.Add(media.ObjectKey);
        mediaKeys.Add((track.Id, media.ObjectKey));

        if (!objects.TryGetValue(media.ObjectKey, out var objectMetadata))
        {
            objectMetadata = await _storage.GetMetadataAsync(media.ObjectKey, ct);
            if (objectMetadata is null)
            {
                AddFinding(run, track.Id, "database_row_without_object", "error", media.ObjectKey,
                    "No storage object matches the database media row.");
                if (isPublished) publishedFailures.Add(track.Id);
                if (remediate && media.State == TrackMediaStates.Ready)
                    await DemoteAsync(media, "media_object_missing", "The validated storage object is missing.", ct);
                return;
            }
        }

        if (objectMetadata.SizeBytes == 0)
            AddFinding(run, track.Id, "zero_byte_object", "error", media.ObjectKey, "The storage object is empty.");
        if (media.SizeBytes.HasValue && media.SizeBytes != objectMetadata.SizeBytes)
            AddFinding(run, track.Id, "size_mismatch", "error", media.ObjectKey, "Stored size metadata differs from storage.");

        // S3/R2 ListObjectsV2 carries no content type; resolve via HEAD or the
        // validated row before judging, and never flag an unknown type.
        var contentType = objectMetadata.ContentType;
        if (string.IsNullOrWhiteSpace(contentType))
            contentType = (await _storage.GetMetadataAsync(media.ObjectKey, ct))?.ContentType;
        if (string.IsNullOrWhiteSpace(contentType))
            contentType = media.ContentType;
        if (!string.IsNullOrWhiteSpace(contentType) && !SupportedTypes.Contains(contentType))
            AddFinding(run, track.Id, "wrong_mime_type", "error", media.ObjectKey, "The storage object has an unsupported content type.");

        if (!media.DurationMilliseconds.HasValue || media.DurationMilliseconds <= 0)
            AddFinding(run, track.Id, "invalid_or_missing_duration", "error", media.ObjectKey, "Validated duration is missing or invalid.");

        if (remediate && media.State is TrackMediaStates.Uploaded or TrackMediaStates.Failed or TrackMediaStates.Validating)
        {
            if (media.State != TrackMediaStates.Validating)
            {
                media = await _stateMachine.TransitionAsync(
                    track.Id, media.ConcurrencyToken, TrackMediaStates.Validating, new MediaStateMetadata(), ct);
            }
            var candidate = await _validation.ValidateAsync(new MediaValidationRequest(
                track.Id, media.ObjectKey!, media.SizeBytes, media.ContentType, media.ChecksumSha256), ct);
            if (candidate.IsValid)
            {
                media = await _stateMachine.TransitionAsync(
                    track.Id,
                    media.ConcurrencyToken,
                    TrackMediaStates.Ready,
                    new MediaStateMetadata(
                        ValidatedAtUtc: _clock.GetUtcNow().UtcDateTime,
                        SizeBytes: candidate.SizeBytes,
                        ContentType: candidate.ContentType,
                        ChecksumSha256: candidate.ChecksumSha256,
                        DurationMilliseconds: candidate.DurationMilliseconds,
                        ValidationVersion: candidate.ValidationVersion),
                    ct);
            }
            else if (!candidate.DependencyUnavailable)
            {
                AddFinding(run, track.Id, candidate.FailureCode ?? "media_validation_failed", "error", media.ObjectKey,
                    candidate.SafeDetail ?? "The media candidate failed validation.");
                await DemoteAsync(media, candidate.FailureCode ?? "media_validation_failed",
                    candidate.SafeDetail ?? "Media validation failed.", ct);
            }
            else
            {
                AddFinding(run, track.Id, "validation_dependency_unavailable", "warning", media.ObjectKey,
                    "A transient dependency prevented media validation; the media state was left unchanged.", "none");
            }
        }

        else if (media.State == TrackMediaStates.Ready)
        {
            var validation = await _validation.ValidateAsync(new MediaValidationRequest(
                track.Id, media.ObjectKey, media.SizeBytes, media.ContentType, media.ChecksumSha256), ct);
            if (validation.DependencyUnavailable)
            {
                AddFinding(run, track.Id, "validation_dependency_unavailable", "warning", media.ObjectKey,
                    "A transient dependency prevented Ready revalidation; the media state was left unchanged.", "none");
            }
            else if (!validation.IsValid)
            {
                var findingType = validation.FailureCode switch
                {
                    "checksum_mismatch" => "checksum_mismatch",
                    "media_object_missing" => "database_row_without_object",
                    "unsupported_content_type" or "content_type_mismatch" => "wrong_mime_type",
                    "duration_out_of_range" => "invalid_or_missing_duration",
                    _ => "ready_track_failed_playback",
                };
                AddFinding(run, track.Id, findingType, "error", media.ObjectKey,
                    validation.SafeDetail ?? "The Ready track failed production-path validation.");
                if (isPublished) publishedFailures.Add(track.Id);
                if (remediate)
                    await DemoteAsync(media, validation.FailureCode ?? "media_validation_failed",
                        validation.SafeDetail ?? "Media validation failed.", ct);
            }
            else if (remediate)
            {
                await _stateMachine.RefreshValidationAsync(
                    track.Id,
                    media.ConcurrencyToken,
                    new MediaStateMetadata(
                        ValidatedAtUtc: _clock.GetUtcNow().UtcDateTime,
                        SizeBytes: validation.SizeBytes,
                        ContentType: validation.ContentType,
                        ChecksumSha256: validation.ChecksumSha256,
                        DurationMilliseconds: validation.DurationMilliseconds,
                        ValidationVersion: validation.ValidationVersion),
                    ct);
            }
        }
    }

    public async Task<IReadOnlyList<MediaReconciliationSummary>> GetRunsAsync(int take, CancellationToken ct = default) =>
        await _db.MediaReconciliationRuns.AsNoTracking()
            .OrderByDescending(x => x.StartedAtUtc)
            .Take(Math.Clamp(take, 1, 100))
            .Select(x => new MediaReconciliationSummary(
                x.Id, x.Status, x.StartedAtUtc, x.CompletedAtUtc, x.TracksInspected,
                x.ObjectsInspected, x.FindingCount, x.UnresolvedPublishedTrackFailures))
            .ToListAsync(ct);

    public async Task<MediaReconciliationReport?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _db.MediaReconciliationRuns.AsNoTracking()
            .Include(x => x.Findings)
            .SingleOrDefaultAsync(x => x.Id == runId, ct);
        return run is null
            ? null
            : new MediaReconciliationReport(
                ToSummary(run),
                run.Findings.OrderBy(x => x.CreatedAtUtc)
                    .Select(x => new MediaReconciliationFindingDto(
                        x.Id, x.TrackId, x.FindingType, x.Severity, x.Detail, x.Resolution, x.CreatedAtUtc))
                    .ToList());
    }

    private async Task DemoteAsync(TrackMedia media, string code, string detail, CancellationToken ct)
    {
        var target = code is "checksum_mismatch" or "media_parse_failed" or "decode_probe_failed"
            ? TrackMediaStates.Quarantined
            : TrackMediaStates.Failed;
        await _stateMachine.TransitionAsync(
            media.TrackId, media.ConcurrencyToken, target,
            new MediaStateMetadata(FailureCode: code, FailureDetail: detail), ct);
    }

    private void AddFinding(
        MediaReconciliationRun run,
        Guid? trackId,
        string type,
        string severity,
        string? objectKey,
        string detail,
        string resolution = "operator_action_required")
    {
        var finding = new MediaReconciliationFinding
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            TrackId = trackId,
            FindingType = type,
            Severity = severity,
            ObjectKey = objectKey,
            Detail = detail,
            Resolution = resolution,
            CreatedAtUtc = _clock.GetUtcNow().UtcDateTime,
        };
        run.Findings.Add(finding);
        _db.MediaReconciliationFindings.Add(finding);
    }

    private static bool LooksLikeLegacyLocation(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/stream/", StringComparison.OrdinalIgnoreCase));

    private static MediaReconciliationSummary ToSummary(MediaReconciliationRun run) =>
        new(run.Id, run.Status, run.StartedAtUtc, run.CompletedAtUtc, run.TracksInspected,
            run.ObjectsInspected, run.FindingCount, run.UnresolvedPublishedTrackFailures);
}
