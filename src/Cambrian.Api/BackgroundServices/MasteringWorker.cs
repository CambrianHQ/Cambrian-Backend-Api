using System.Text.Json;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.ReleaseReady;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Options;
using Sentry;

namespace Cambrian.Api.BackgroundServices;

/// <summary>
/// In-process Release Ready mastering queue. Polls roughly every 3 seconds, claims
/// the next queued job race-safely (<see cref="IMasteringJobRepository.ClaimNextQueuedAsync"/>),
/// opens a fresh DI scope per job, and runs the configured <see cref="IMasteringEngine"/>.
///
/// <para>
/// One-shot engines (ffmpeg) upload the WAV+MP3, record measured loudness, and finish
/// <c>done</c>. Preview engines (Tonn) store a preview and stop at <c>awaiting_approval</c>
/// (the final master is retrieved later in <c>ApproveAsync</c>). A single retry is allowed:
/// the first failure requeues (<c>RetryCount=1</c>); the second marks the job <c>failed</c>
/// and reports to Sentry. ffmpeg concurrency is bound to one job at a time.
/// </para>
///
/// <para>Assumes a single worker instance — the work-list is the <c>queued</c> rows, and
/// claims are guarded by a conditional update so concurrent ticks cannot double-claim.</para>
/// </summary>
public sealed class MasteringWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    // Bound ffmpeg (CPU/IO heavy, spawns a subprocess) to one job at a time.
    private static readonly SemaphoreSlim FfmpegGate = new(1, 1);

    private const string MasterWavKeyFmt = "release-ready/master/{0}/master.wav";
    private const string MasterMp3KeyFmt = "release-ready/master/{0}/master.mp3";
    private const string PreviewKeyFmt = "release-ready/preview/{0}/preview.wav";

    private readonly IServiceScopeFactory _scopes;
    private readonly MasteringOptions _options;
    private readonly ILogger<MasteringWorker> _logger;

    public MasteringWorker(IServiceScopeFactory scopes, IOptions<MasteringOptions> options, ILogger<MasteringWorker> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EVENT: MasteringWorkerStarted pollSeconds:{Poll}", PollInterval.TotalSeconds);

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await DrainQueueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutting down
            }
            catch (Exception ex)
            {
                // Never let a tick kill the loop.
                _logger.LogError(ex, "EVENT: MasteringWorkerTickFailed");
            }
        }
    }

    // Claim and process queued jobs until the queue empties for this tick.
    private async Task DrainQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var scope = _scopes.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IMasteringJobRepository>();

            var job = await jobs.ClaimNextQueuedAsync(ct);
            if (job is null)
                return; // queue empty

            await ProcessJobAsync(scope.ServiceProvider, job, ct);
        }
    }

    private async Task ProcessJobAsync(IServiceProvider sp, MasteringJob job, CancellationToken ct)
    {
        var jobs = sp.GetRequiredService<IMasteringJobRepository>();
        var engine = sp.GetRequiredService<IMasteringEngine>();
        var storage = sp.GetRequiredService<IObjectStorage>();
        var leaseId = job.ProcessingLeaseId
            ?? throw new InvalidOperationException($"Claimed mastering job {job.Id} has no processing lease.");

        try
        {
            await RunWithHeartbeatAsync(
                jobs,
                job,
                leaseId,
                async runCt =>
                {
                    if (engine.RequiresApproval)
                        await RunPreviewEngineAsync(sp, jobs, engine, storage, job, leaseId, runCt);
                    else
                        await RunOneShotEngineAsync(sp, jobs, engine, storage, job, leaseId, runCt);
                },
                ct);
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(jobs, job, leaseId, ex, ct);
        }
    }

    // ── ffmpeg (one-shot): master inline, upload WAV+MP3, run pipeline stages, status=done ──
    private async Task RunOneShotEngineAsync(
        IServiceProvider sp, IMasteringJobRepository jobs, IMasteringEngine engine, IObjectStorage storage,
        MasteringJob job, Guid leaseId, CancellationToken ct)
    {
        // Resumable: a retried job whose master already uploaded skips the heavy
        // audio work and goes straight to the remaining pipeline stages.
        if (string.IsNullOrWhiteSpace(job.MasteredWavKey))
        {
            await FfmpegGate.WaitAsync(ct);
            try
            {
                using var source = await storage.OpenReadAsync(job.SourceKey)
                    ?? throw new InvalidOperationException($"Source not found at {job.SourceKey}.");
                using var cover = await OpenCoverArtAsync(sp, storage, job, ct);
                var metadata = await BuildReleaseMetadataAsync(sp, job, ct);

                var result = await engine.MasterAsync(
                    new MasteringEngineRequest
                    {
                        Source = source.Stream,
                        SourceFileName = job.SourceFileName ?? "audio",
                        TargetLufs = job.TargetLufs,
                        TargetTruePeakDbtp = job.TargetTruePeakDbtp,
                        Metadata = metadata,
                        CoverArt = cover?.Stream,
                        CoverArtFileName = Path.GetFileName(job.CoverArtKey),
                    },
                    ct);

                await UploadMastersAsync(storage, job, result);
                job.InputLufs = result.InputLufs;
                job.OutputLufs = result.OutputLufs;
                job.OutputTruePeakDbtp = result.OutputTruePeakDbtp;
                if (!await jobs.UpdateLeaseOwnedAsync(job, leaseId, ct))
                    throw new InvalidOperationException("Mastering job processing lease was lost before outputs were saved.");
            }
            finally
            {
                FfmpegGate.Release();
            }
        }

        // Release-pipeline jobs run the Metadata → Cover → Disclosure → Provenance
        // stages after mastering; each transition persists for GET /api/jobs/{id}.
        if (job.Kind == "release_pipeline")
        {
            var pipeline = sp.GetRequiredService<ITrackReleasePipelineService>();
            await pipeline.RunPostMasteringStagesAsync(job, ct);
        }

        job.Status = "done";
        job.CompletedAt = DateTime.UtcNow;
        job.Error = null;
        if (!await jobs.MarkDoneAsync(job, leaseId, ct))
            throw new InvalidOperationException("Mastering job processing lease was lost before completion.");

        _logger.LogInformation(
            "EVENT: MasteringJobDone jobId:{JobId} engine:{Engine} kind:{Kind} wav:{Wav} mp3:{Mp3}",
            job.Id, engine.Name, job.Kind, job.MasteredWavKey is not null, job.MasteredMp3Key is not null);
    }

    // ── Tonn (preview): produce a preview, store it, status=awaiting_approval ──
    private async Task RunPreviewEngineAsync(
        IServiceProvider sp, IMasteringJobRepository jobs, IMasteringEngine engine, IObjectStorage storage,
        MasteringJob job, Guid leaseId, CancellationToken ct)
    {
        // RoEx fetches the source from a signed URL.
        var sourceUrl = storage.GenerateSignedUrl(job.SourceKey);
        var metadata = await BuildReleaseMetadataAsync(sp, job, ct);

        var result = await engine.MasterAsync(
            new MasteringEngineRequest
            {
                SourceUrl = sourceUrl,
                SourceFileName = job.SourceFileName ?? "audio",
                TargetLufs = job.TargetLufs,
                TargetTruePeakDbtp = job.TargetTruePeakDbtp,
                Metadata = metadata,
            },
            ct);

        // Store the preview so it survives behind our own signed URL.
        if (result.Wav is { Length: > 0 } previewBytes)
        {
            var previewKey = string.Format(PreviewKeyFmt, job.Id);
            using var ms = new MemoryStream(previewBytes);
            await storage.UploadAsync(ms, previewKey, "audio/wav");
            job.PreviewKey = previewKey;
        }
        else if (!string.IsNullOrWhiteSpace(result.PreviewUrl))
        {
            // Engine returned only a remote preview URL; persist it as the preview key.
            job.PreviewKey = result.PreviewUrl;
        }

        job.EngineRef = result.EngineRef;
        job.Status = "awaiting_approval";
        job.Error = null;
        if (!await jobs.MarkAwaitingApprovalAsync(job, leaseId, ct))
            throw new InvalidOperationException("Mastering job processing lease was lost before preview completion.");

        _logger.LogInformation(
            "EVENT: MasteringPreviewReady jobId:{JobId} engine:{Engine} engineRef:{EngineRef}",
            job.Id, engine.Name, result.EngineRef);
    }

    private static async Task UploadMastersAsync(IObjectStorage storage, MasteringJob job, MasteringEngineResult result)
    {
        if (result.Wav is { Length: > 0 } wav)
        {
            var wavKey = string.Format(MasterWavKeyFmt, job.Id);
            using var ms = new MemoryStream(wav);
            await storage.UploadAsync(ms, wavKey, "audio/wav");
            job.MasteredWavKey = wavKey;
        }

        if (result.Mp3 is { Length: > 0 } mp3)
        {
            var mp3Key = string.Format(MasterMp3KeyFmt, job.Id);
            using var ms = new MemoryStream(mp3);
            await storage.UploadAsync(ms, mp3Key, "audio/mpeg");
            job.MasteredMp3Key = mp3Key;
        }
    }

    // ── One retry, then failed + Sentry ──
    private async Task HandleFailureAsync(IMasteringJobRepository jobs, MasteringJob job, Guid leaseId, Exception ex, CancellationToken ct)
    {
        if (job.RetryCount < Math.Max(0, _options.MaxRetryCount))
        {
            var nextRetryCount = job.RetryCount + 1;
            if (!await jobs.RequeueForRetryAsync(job.Id, leaseId, nextRetryCount, Truncate(ex.Message), ct))
            {
                _logger.LogWarning(
                    "EVENT: MasteringJobRetrySkippedLeaseLost jobId:{JobId} retryCount:{Retry}",
                    job.Id, nextRetryCount);
                return;
            }
            job.RetryCount = nextRetryCount;
            _logger.LogWarning(ex, "EVENT: MasteringJobRetry jobId:{JobId} retryCount:{Retry}", job.Id, job.RetryCount);
            return;
        }

        if (!await jobs.MarkFailedAsync(job.Id, leaseId, Truncate(ex.Message), ct))
        {
            _logger.LogWarning("EVENT: MasteringJobFailSkippedLeaseLost jobId:{JobId}", job.Id);
            return;
        }

        SentrySdk.CaptureException(ex);
        Cambrian.Application.Observability.CambrianMetrics.ReleaseReadyJobFailed.Add(1);
        _logger.LogError(ex, "EVENT: release_ready_job_failed jobId:{JobId}", job.Id);
    }

    private static string Truncate(string s) => s.Length <= 1000 ? s : s[..1000];

    private async Task RunWithHeartbeatAsync(
        IMasteringJobRepository jobs,
        MasteringJob job,
        Guid leaseId,
        Func<CancellationToken, Task> work,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workTask = work(linked.Token);
        var heartbeatTask = HeartbeatLoopAsync(jobs, job.Id, leaseId, linked.Token);

        var completed = await Task.WhenAny(workTask, heartbeatTask);
        if (completed == heartbeatTask)
        {
            linked.Cancel();
            await heartbeatTask;
        }

        try
        {
            await workTask;
        }
        finally
        {
            linked.Cancel();
            try { await heartbeatTask; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || linked.IsCancellationRequested) { }
        }
    }

    private async Task HeartbeatLoopAsync(IMasteringJobRepository jobs, Guid jobId, Guid leaseId, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.ProcessingHeartbeatSeconds));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (!await jobs.HeartbeatAsync(jobId, leaseId, ct))
                throw new InvalidOperationException($"Processing lease for mastering job {jobId} is no longer active.");
        }
    }

    private static async Task<ReleaseMetadata> BuildReleaseMetadataAsync(IServiceProvider sp, MasteringJob job, CancellationToken ct)
    {
        Track? track = null;
        if (job.TrackId is Guid trackId)
        {
            var tracks = sp.GetRequiredService<ITrackRepository>();
            track = await tracks.GetByIdAsync(trackId);
        }

        var report = DeserializeValidation(job.ValidationReportJson);
        return new ReleaseMetadata
        {
            Title = FirstNonEmpty(track?.Title, report?.Metadata.Title),
            Artist = FirstNonEmpty(track?.Creator?.DisplayName, track?.CreatorEntity?.DisplayName, report?.Metadata.Artist),
            Album = FirstNonEmpty(report?.Metadata.Album, track?.Title),
            Date = job.CreatedAt.Year.ToString(),
            Genre = FirstNonEmpty(track?.PrimaryGenre, track?.Genre, track?.Subgenre),
            Comment = "Release Ready Beta: loudness-normalized export with embedded MP3 metadata/artwork.",
        };
    }

    private static async Task<StorageFile?> OpenCoverArtAsync(
        IServiceProvider sp,
        IObjectStorage storage,
        MasteringJob job,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(job.CoverArtKey))
            return await storage.OpenReadAsync(job.CoverArtKey!);

        if (job.TrackId is not Guid trackId)
            return null;

        var tracks = sp.GetRequiredService<ITrackRepository>();
        var track = await tracks.GetByIdAsync(trackId);
        return string.IsNullOrWhiteSpace(track?.CoverArtUrl)
            ? null
            : await storage.OpenReadAsync(track.CoverArtUrl!);
    }

    private static ValidationReport? DeserializeValidation(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try { return JsonSerializer.Deserialize<ValidationReport>(json, JsonOpts); }
        catch (JsonException) { return null; }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
