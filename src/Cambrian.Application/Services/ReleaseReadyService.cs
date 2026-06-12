using System.Text.Json;
using Cambrian.Application.DTOs.ReleaseReady;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

/// <summary>
/// Orchestrates the Release Ready flow: upload+validate → submit (ffmpeg charges
/// here) → optional approve (Tonn charges + finalizes here) → status → download.
/// The controller is a thin HTTP adapter over this service; all credit/state rules
/// live here. Typed exceptions (<see cref="KeyNotFoundException"/> /
/// <see cref="InvalidOperationException"/> / <see cref="InsufficientCreditsException"/>)
/// map to HTTP status by the controller.
/// </summary>
public sealed class ReleaseReadyService : IReleaseReadyService
{
    // Storage key templates (frozen contract — never modify the source key).
    private const string SourceKeyFmt = "release-ready/source/{0}{1}";
    private const string MasterWavKeyFmt = "release-ready/master/{0}/master.wav";
    private const string MasterMp3KeyFmt = "release-ready/master/{0}/master.mp3";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IReleaseValidationService _validation;
    private readonly IReleaseCreditService _credits;
    private readonly IMasteringJobRepository _jobs;
    private readonly IMasteringEngine _engine;
    private readonly IObjectStorage _storage;
    private readonly ITrackRepository _tracks;
    private readonly ITrackReleasePipelineService _pipeline;
    private readonly ITrackReadinessCache _readinessCache;
    private readonly ILogger<ReleaseReadyService> _logger;

    public ReleaseReadyService(
        IReleaseValidationService validation,
        IReleaseCreditService credits,
        IMasteringJobRepository jobs,
        IMasteringEngine engine,
        IObjectStorage storage,
        ITrackRepository tracks,
        ITrackReleasePipelineService pipeline,
        ITrackReadinessCache readinessCache,
        ILogger<ReleaseReadyService> logger)
    {
        _validation = validation;
        _credits = credits;
        _jobs = jobs;
        _engine = engine;
        _storage = storage;
        _tracks = tracks;
        _pipeline = pipeline;
        _readinessCache = readinessCache;
        _logger = logger;
    }

    public Task<CreditStatusDto> GetCreditsAsync(string userId, CancellationToken ct = default) =>
        _credits.GetStatusAsync(userId, ct);

    public async Task<ValidateResponse> ValidateAndCreateAsync(ReleaseReadyUploadInput input, CancellationToken ct = default)
    {
        if (input.Audio is null)
            throw new InvalidOperationException("An audio file is required.");

        // Buffer the audio so it stays seekable across validation + upload.
        var audio = await ToSeekableAsync(input.Audio, ct);

        var metadata = _validation.ValidateMetadata(audio, input.AudioFileName);

        ArtworkValidationResult artwork;
        Stream? artworkBuffer = null;
        if (input.Artwork is not null)
        {
            artworkBuffer = await ToSeekableAsync(input.Artwork, ct);
            artwork = _validation.ValidateArtwork(artworkBuffer, input.ArtworkFileName);
        }
        else
        {
            artwork = _validation.ValidateArtwork(null, null);
        }

        var report = new ValidationReport { Metadata = metadata, Artwork = artwork };

        var jobId = Guid.NewGuid();
        var ext = SafeExt(input.AudioFileName);
        var sourceKey = string.Format(SourceKeyFmt, jobId, ext);

        // Persist the source — read-only thereafter.
        audio.Position = 0;
        await _storage.UploadAsync(audio, sourceKey, GuessAudioContentType(ext));

        // Persist DDEX AI-disclosure on the track when one is supplied (owner-scoped).
        if (input.TrackId is Guid trackId)
            await PersistAiDisclosureAsync(trackId, input.UserId, input.AiGenerated, input.AiDisclosure);

        var job = new Domain.Entities.MasteringJob
        {
            Id = jobId,
            CreatorId = input.UserId,
            TrackId = input.TrackId,
            Engine = _engine.Name,
            Status = "validated",
            SourceKey = sourceKey,
            SourceFileName = input.AudioFileName,
            TargetLufs = input.TargetLufs ?? -14.0,
            ValidationReportJson = JsonSerializer.Serialize(report, JsonOpts),
            CreatedAt = DateTime.UtcNow,
        };
        await _jobs.AddAsync(job, ct);

        _logger.LogInformation(
            "EVENT: ReleaseReadyValidated jobId:{JobId} userId:{UserId} engine:{Engine} metadataPassed:{MetaOk} artworkPassed:{ArtOk}",
            jobId, input.UserId, _engine.Name, metadata.Passed, artwork.Passed);

        artworkBuffer?.Dispose();

        return new ValidateResponse
        {
            JobId = jobId,
            Engine = _engine.Name,
            RequiresApproval = _engine.RequiresApproval,
            Validation = report,
        };
    }

    public async Task<JobDto> SubmitAsync(Guid jobId, string userId, CancellationToken ct = default)
    {
        var job = await _jobs.GetForOwnerAsync(jobId, userId, ct)
            ?? throw new KeyNotFoundException($"Mastering job {jobId} not found.");

        if (job.Status != "validated")
            throw new InvalidOperationException($"Job is in '{job.Status}' state and cannot be submitted.");

        // One-shot engines (ffmpeg) charge on submit; preview engines (Tonn) charge on approve.
        if (!_engine.RequiresApproval)
        {
            var charged = await _credits.TryChargeAsync(jobId, userId, ct);
            if (!charged)
                throw new InsufficientCreditsException();
        }

        job.Status = "queued";
        await _jobs.UpdateAsync(job, ct);

        _logger.LogInformation(
            "EVENT: ReleaseReadySubmitted jobId:{JobId} userId:{UserId} engine:{Engine} requiresApproval:{Approval}",
            jobId, userId, _engine.Name, _engine.RequiresApproval);

        return MapToDto(job);
    }

    public async Task<JobDto> ApproveAsync(Guid jobId, string userId, CancellationToken ct = default)
    {
        var job = await _jobs.GetForOwnerAsync(jobId, userId, ct)
            ?? throw new KeyNotFoundException($"Mastering job {jobId} not found.");

        if (!_engine.RequiresApproval)
            throw new InvalidOperationException("This engine masters in one shot; there is no approval step.");

        if (job.Status != "awaiting_approval")
            throw new InvalidOperationException($"Job is in '{job.Status}' state and cannot be approved.");

        if (string.IsNullOrWhiteSpace(job.EngineRef))
            throw new InvalidOperationException("Job has no engine reference to finalize.");

        // Preview engines charge on approve.
        var charged = await _credits.TryChargeAsync(jobId, userId, ct);
        if (!charged)
            throw new InsufficientCreditsException();

        // Retrieve the final master from the engine and persist the outputs.
        var result = await _engine.FinalizeAsync(
            new MasteringEngineRequest
            {
                SourceFileName = job.SourceFileName ?? "audio",
                TargetLufs = job.TargetLufs,
                TargetTruePeakDbtp = job.TargetTruePeakDbtp,
            },
            job.EngineRef,
            ct);

        await StoreOutputsAsync(job, result, ct);

        // Release-pipeline jobs run their remaining stages once the master is approved
        // (preview engines reach this path instead of the worker's one-shot path).
        if (job.Kind == "release_pipeline")
            await _pipeline.RunPostMasteringStagesAsync(job, ct);

        job.Status = "done";
        job.CompletedAt = DateTime.UtcNow;
        await _jobs.UpdateAsync(job, ct);

        _logger.LogInformation("EVENT: ReleaseReadyApproved jobId:{JobId} userId:{UserId}", jobId, userId);

        return MapToDto(job);
    }

    public async Task<JobDto?> GetJobAsync(Guid jobId, string userId, CancellationToken ct = default)
    {
        var job = await _jobs.GetForOwnerAsync(jobId, userId, ct);
        return job is null ? null : MapToDto(job);
    }

    public async Task<IReadOnlyList<JobSummaryDto>> ListJobsAsync(string userId, int take, CancellationToken ct = default)
    {
        var jobs = await _jobs.ListByCreatorAsync(userId, take, ct);
        return jobs.Select(j => new JobSummaryDto
        {
            Id = j.Id,
            Status = j.Status,
            Engine = j.Engine,
            CreatedAt = j.CreatedAt,
            CompletedAt = j.CompletedAt,
        }).ToList();
    }

    public async Task<MasteringDownload?> GetDownloadAsync(Guid jobId, string userId, string format, CancellationToken ct = default)
    {
        var job = await _jobs.GetForOwnerAsync(jobId, userId, ct);
        if (job is null)
            return null;

        var wantWav = !string.Equals(format, "mp3", StringComparison.OrdinalIgnoreCase);
        var key = wantWav ? job.MasteredWavKey : job.MasteredMp3Key;
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var ext = wantWav ? ".wav" : ".mp3";
        var contentType = wantWav ? "audio/wav" : "audio/mpeg";
        var fileName = $"master-{jobId}{ext}";

        // Prefer a signed URL (S3); fall back to a streamed body (local).
        var signed = _storage.GenerateDownloadUrl(key!, fileName);
        if (!string.IsNullOrWhiteSpace(signed) &&
            signed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return new MasteringDownload
            {
                SignedUrl = signed,
                ContentType = contentType,
                FileName = fileName,
            };
        }

        var file = await _storage.OpenReadAsync(key!);
        if (file is null)
            return null;

        return new MasteringDownload
        {
            Content = file.Stream,
            ContentType = file.ContentType ?? contentType,
            FileName = fileName,
        };
    }

    // ── Helpers shared with the worker semantics ──

    /// <summary>Upload the engine's WAV/MP3 to the master keys and record measured loudness.</summary>
    private async Task StoreOutputsAsync(Domain.Entities.MasteringJob job, MasteringEngineResult result, CancellationToken ct)
    {
        if (result.Wav is { Length: > 0 } wav)
        {
            var wavKey = string.Format(MasterWavKeyFmt, job.Id);
            using var ms = new MemoryStream(wav);
            await _storage.UploadAsync(ms, wavKey, "audio/wav");
            job.MasteredWavKey = wavKey;
        }

        if (result.Mp3 is { Length: > 0 } mp3)
        {
            var mp3Key = string.Format(MasterMp3KeyFmt, job.Id);
            using var ms = new MemoryStream(mp3);
            await _storage.UploadAsync(ms, mp3Key, "audio/mpeg");
            job.MasteredMp3Key = mp3Key;
        }

        if (result.InputLufs is not null) job.InputLufs = result.InputLufs;
        if (result.OutputLufs is not null) job.OutputLufs = result.OutputLufs;
        if (result.OutputTruePeakDbtp is not null) job.OutputTruePeakDbtp = result.OutputTruePeakDbtp;
    }

    private async Task PersistAiDisclosureAsync(Guid trackId, string userId, bool aiGenerated, string? aiDisclosure)
    {
        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null || !string.Equals(track.CreatorId, userId, StringComparison.Ordinal))
            return; // silently skip when the track is absent or not owned

        track.AiGenerated = aiGenerated;
        track.AiDisclosureDdex = aiDisclosure;
        await _tracks.UpdateAsync(track);
        _readinessCache.Invalidate(trackId);
    }

    private JobDto MapToDto(Domain.Entities.MasteringJob job)
    {
        ValidationReport? report = null;
        if (!string.IsNullOrWhiteSpace(job.ValidationReportJson))
        {
            try { report = JsonSerializer.Deserialize<ValidationReport>(job.ValidationReportJson, JsonOpts); }
            catch (JsonException) { /* tolerate legacy/garbled rows */ }
        }

        string? previewUrl = null;
        if (!string.IsNullOrWhiteSpace(job.PreviewKey))
        {
            var signed = _storage.GenerateSignedUrl(job.PreviewKey!);
            previewUrl = string.IsNullOrWhiteSpace(signed) ? null : signed;
        }

        return new JobDto
        {
            Id = job.Id,
            TrackId = job.TrackId,
            Engine = job.Engine,
            Status = job.Status,
            RequiresApproval = _engine.RequiresApproval,
            Validation = report,
            InputLufs = job.InputLufs,
            OutputLufs = job.OutputLufs,
            OutputTruePeakDbtp = job.OutputTruePeakDbtp,
            PreviewUrl = previewUrl,
            WavReady = !string.IsNullOrWhiteSpace(job.MasteredWavKey),
            Mp3Ready = !string.IsNullOrWhiteSpace(job.MasteredMp3Key),
            Error = job.Error,
            CreatedAt = job.CreatedAt,
            CompletedAt = job.CompletedAt,
        };
    }

    private static async Task<Stream> ToSeekableAsync(Stream source, CancellationToken ct)
    {
        if (source.CanSeek)
        {
            source.Position = 0;
            return source;
        }

        var ms = new MemoryStream();
        await source.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    private static string SafeExt(string? name)
    {
        var ext = Path.GetExtension(name ?? "");
        return string.IsNullOrWhiteSpace(ext) || ext.Length > 6 ? ".audio" : ext.ToLowerInvariant();
    }

    private static string GuessAudioContentType(string ext) => ext.ToLowerInvariant() switch
    {
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".aac" => "audio/aac",
        ".ogg" => "audio/ogg",
        ".m4a" => "audio/mp4",
        _ => "application/octet-stream",
    };
}
