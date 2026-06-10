using System.Text.Json;
using Cambrian.Application.DTOs.Readiness;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Provenance;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

/// <inheritdoc cref="ITrackReleasePipelineService" />
public sealed class TrackReleasePipelineService : ITrackReleasePipelineService
{
    private const string DisclosureKeyFmt = "release-ready/disclosure/{0}.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ITrackRepository _tracks;
    private readonly IMasteringJobRepository _jobs;
    private readonly ITrackAuthorshipRepository _authorship;
    private readonly IReleaseCreditService _credits;
    private readonly IObjectStorage _storage;
    private readonly IReleaseValidationService _validation;
    private readonly IProvenanceSigner _signer;
    private readonly IProvenanceService _provenance;
    private readonly ITrackReadinessCache _readinessCache;
    private readonly ILogger<TrackReleasePipelineService> _logger;

    public TrackReleasePipelineService(
        ITrackRepository tracks,
        IMasteringJobRepository jobs,
        ITrackAuthorshipRepository authorship,
        IReleaseCreditService credits,
        IObjectStorage storage,
        IReleaseValidationService validation,
        IProvenanceSigner signer,
        IProvenanceService provenance,
        ITrackReadinessCache readinessCache,
        ILogger<TrackReleasePipelineService> logger)
    {
        _tracks = tracks;
        _jobs = jobs;
        _authorship = authorship;
        _credits = credits;
        _storage = storage;
        _validation = validation;
        _signer = signer;
        _provenance = provenance;
        _readinessCache = readinessCache;
        _logger = logger;
    }

    public async Task<StartReleaseJobResult> StartAsync(Guid trackId, string userId, CancellationToken ct = default)
    {
        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null || !string.Equals(track.CreatorId, userId, StringComparison.Ordinal))
            throw new KeyNotFoundException("Track not found.");

        if (string.IsNullOrWhiteSpace(track.AudioUrl))
            throw new InvalidOperationException("This track has no stored audio to master.");

        // Resolve the content hash for idempotency. Tracks uploaded before §9 may
        // not be hashed yet — hash the stored bytes once and persist.
        var contentHash = track.ContentHash;
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            using var file = await _storage.OpenReadAsync(track.AudioUrl!)
                ?? throw new InvalidOperationException("The track's stored audio could not be read.");
            using var buffer = new MemoryStream();
            await file.Stream.CopyToAsync(buffer, ct);
            contentHash = ContentHashing.ComputeSha256Hex(buffer);

            track.ContentHash = contentHash;
            await _tracks.UpdateAsync(track);
        }

        // Idempotency: a live (non-failed) job for the same audio means the work is
        // already done or in flight — warn instead of double-charging.
        var existing = await _jobs.GetActiveByTrackAndHashAsync(trackId, contentHash!, ct);
        if (existing is not null)
        {
            _logger.LogInformation(
                "EVENT: ReleasePipelineCoalesced trackId:{TrackId} jobId:{JobId} status:{Status}",
                trackId, existing.Id, existing.Status);

            return new StartReleaseJobResult
            {
                JobId = existing.Id,
                Created = false,
                Warning = "This audio has already been processed (or is in progress). "
                          + "No credit was charged — upload new audio to run the pipeline again.",
            };
        }

        var job = new MasteringJob
        {
            Id = Guid.NewGuid(),
            CreatorId = userId,
            TrackId = trackId,
            Kind = "release_pipeline",
            Status = "validated",
            Stage = "mastering",
            SourceKey = track.AudioUrl!,
            SourceFileName = Path.GetFileName(track.AudioUrl),
            ContentHash = contentHash,
            CreatedAt = DateTime.UtcNow,
        };
        await _jobs.AddAsync(job, ct);

        // Atomic count-and-charge (serializable). On failure the draft row is removed
        // so it can't shadow future idempotency probes.
        var charged = await _credits.TryChargeAsync(job.Id, userId, ct);
        if (!charged)
        {
            job.Status = "failed";
            job.Error = "insufficient_credits";
            job.CompletedAt = DateTime.UtcNow;
            await _jobs.UpdateAsync(job, ct);
            throw new InsufficientCreditsException();
        }

        AppendStage(job, "mastering", "started", "Queued for mastering.");
        job.Status = "queued";
        await _jobs.UpdateAsync(job, ct);

        _logger.LogInformation(
            "EVENT: ReleasePipelineStarted trackId:{TrackId} jobId:{JobId} userId:{UserId}",
            trackId, job.Id, userId);

        return new StartReleaseJobResult { JobId = job.Id, Created = true };
    }

    public async Task<ReleaseJobResponse?> GetJobAsync(Guid jobId, string userId, CancellationToken ct = default)
    {
        var job = await _jobs.GetForOwnerAsync(jobId, userId, ct);
        if (job is null)
            return null;

        var artifacts = new List<ReleaseArtifact>();
        AddArtifact(artifacts, "master_wav", job.MasteredWavKey);
        AddArtifact(artifacts, "master_mp3", job.MasteredMp3Key);
        if (job.Kind == "release_pipeline" && HasCompletedStage(job, "disclosure"))
            AddArtifact(artifacts, "disclosure", string.Format(DisclosureKeyFmt, job.Id));

        return new ReleaseJobResponse
        {
            Id = job.Id,
            TrackId = job.TrackId,
            Status = job.Status,
            Stage = job.Stage,
            Stages = ParseHistory(job),
            Artifacts = artifacts,
            Error = job.Error,
            CreatedAt = job.CreatedAt,
            CompletedAt = job.CompletedAt,
        };
    }

    public async Task RunPostMasteringStagesAsync(MasteringJob job, CancellationToken ct = default)
    {
        if (job.TrackId is not Guid trackId)
            throw new InvalidOperationException("Release-pipeline job has no track.");

        var track = await _tracks.GetByIdAsync(trackId)
            ?? throw new InvalidOperationException($"Track {trackId} no longer exists.");
        var authorship = await _authorship.GetByTrackIdAsync(trackId, ct);

        AppendStage(job, "mastering", "completed",
            job.OutputLufs is double lufs ? $"Mastered to {lufs:0.0} LUFS." : "Mastering complete.");

        await RunStageAsync(job, "metadata", ct, () => Task.FromResult(MetadataDetail(track)));
        await RunStageAsync(job, "cover", ct, () => CoverDetailAsync(track));
        await RunStageAsync(job, "disclosure", ct, () => WriteDisclosureArtifactAsync(job, track, authorship));
        await RunStageAsync(job, "provenance", ct, () => EnsureStampedAsync(track, job, ct));

        _readinessCache.Invalidate(trackId);
    }

    // ── Stage running + history ──

    private async Task RunStageAsync(MasteringJob job, string stage, CancellationToken ct, Func<Task<string>> body)
    {
        job.Stage = stage;
        AppendStage(job, stage, "started", null);
        await _jobs.UpdateAsync(job, ct); // persist the transition so GET /api/jobs/{id} sees live progress

        try
        {
            var detail = await body();
            AppendStage(job, stage, "completed", detail);
            await _jobs.UpdateAsync(job, ct);
        }
        catch (Exception ex)
        {
            AppendStage(job, stage, "failed", ex.Message);
            await _jobs.UpdateAsync(job, ct);
            throw; // the worker's failure path marks the job failed → credit released + Sentry
        }
    }

    private static void AppendStage(MasteringJob job, string stage, string status, string? detail)
    {
        var entries = ParseHistory(job).ToList();
        entries.Add(new ReleaseStageEntry { Stage = stage, Status = status, At = DateTime.UtcNow, Detail = detail });
        job.StageHistoryJson = JsonSerializer.Serialize(entries, JsonOpts);
        if (status != "completed")
            job.Stage = stage;
    }

    private static IReadOnlyList<ReleaseStageEntry> ParseHistory(MasteringJob job)
    {
        if (string.IsNullOrWhiteSpace(job.StageHistoryJson))
            return Array.Empty<ReleaseStageEntry>();
        try
        {
            return JsonSerializer.Deserialize<List<ReleaseStageEntry>>(job.StageHistoryJson, JsonOpts)
                   ?? (IReadOnlyList<ReleaseStageEntry>)Array.Empty<ReleaseStageEntry>();
        }
        catch (JsonException)
        {
            return Array.Empty<ReleaseStageEntry>();
        }
    }

    private static bool HasCompletedStage(MasteringJob job, string stage) =>
        ParseHistory(job).Any(e => e.Stage == stage && e.Status == "completed");

    // ── Stage bodies ──

    private static string MetadataDetail(Track track)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(track.Title)) missing.Add("title");
        if (string.IsNullOrWhiteSpace(track.PrimaryGenre) &&
            string.IsNullOrWhiteSpace(track.Genre) &&
            string.IsNullOrWhiteSpace(track.Subgenre)) missing.Add("genre");
        if (string.IsNullOrWhiteSpace(track.Description)) missing.Add("description");
        if (string.IsNullOrWhiteSpace(track.Mood)) missing.Add("mood");
        if (string.IsNullOrWhiteSpace(track.Tempo)) missing.Add("tempo");

        return missing.Count == 0
            ? "All metadata fields present."
            : $"Metadata incomplete — missing: {string.Join(", ", missing)}.";
    }

    private async Task<string> CoverDetailAsync(Track track)
    {
        if (string.IsNullOrWhiteSpace(track.CoverArtUrl))
            return "No cover art on the track (3000×3000 JPEG/PNG required for release).";

        try
        {
            using var file = await _storage.OpenReadAsync(track.CoverArtUrl!);
            if (file is not null)
            {
                var result = _validation.ValidateArtwork(file.Stream, track.CoverArtUrl);
                return result.Passed
                    ? $"Cover art validated at {result.Width}×{result.Height} {result.Format}."
                    : $"Cover art does not meet spec: {string.Join(" ", result.Issues)}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EVENT: ReleasePipelineCoverProbeFailed trackId:{TrackId}", track.Id);
        }

        return "Cover art present but could not be verified.";
    }

    /// <summary>Write the DDEX-aligned AI-disclosure artifact for distributor handoff.</summary>
    private async Task<string> WriteDisclosureArtifactAsync(MasteringJob job, Track track, TrackAuthorship? authorship)
    {
        var disclosure = new
        {
            trackId = track.Id,
            cambrianTrackId = track.CambrianTrackId,
            aiGenerated = track.AiGenerated,
            aiDisclosureDdex = track.AiDisclosureDdex,
            authorshipAiDisclosure = authorship?.AiDisclosure,
            lyricsAuthored = authorship?.LyricsAuthored ?? false,
            commercialRightsVerified = track.CommercialRightsVerified,
            generatedAt = DateTime.UtcNow,
        };

        var json = JsonSerializer.Serialize(disclosure, JsonOpts);
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await _storage.UploadAsync(ms, string.Format(DisclosureKeyFmt, job.Id), "application/json");

        var hasDisclosure = !string.IsNullOrWhiteSpace(track.AiDisclosureDdex)
                            || !string.IsNullOrWhiteSpace(authorship?.AiDisclosure);
        return hasDisclosure
            ? "AI-disclosure artifact written."
            : "AI-disclosure artifact written, but no disclosure is on file — add one before distributing.";
    }

    /// <summary>Issue the free §9 provenance stamp (hash + signature) when missing.</summary>
    private async Task<string> EnsureStampedAsync(Track track, MasteringJob job, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(track.Signature))
            return "Provenance stamp already exists.";

        var contentHash = track.ContentHash ?? job.ContentHash;
        if (string.IsNullOrWhiteSpace(contentHash))
            return "No content hash available — provenance stamp skipped.";

        var stamp = _signer.Sign(contentHash!, DateTime.UtcNow);
        track.ContentHash = contentHash;
        track.Signature = stamp.Signature;
        track.SignedAt = stamp.SignedAt;
        await _tracks.UpdateAsync(track);

        await _provenance.EnsureAnchorPendingAsync(track.Id, contentHash!, ct);
        return $"Provenance stamp issued (key {stamp.KeyId}); on-chain anchoring pending.";
    }

    private void AddArtifact(List<ReleaseArtifact> list, string kind, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        var url = _storage.GenerateSignedUrl(key!);
        if (!string.IsNullOrWhiteSpace(url))
            list.Add(new ReleaseArtifact { Kind = kind, Url = url });
    }
}
