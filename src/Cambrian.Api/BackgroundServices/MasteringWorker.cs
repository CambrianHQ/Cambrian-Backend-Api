using Cambrian.Application.DTOs.ReleaseReady;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
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
    private readonly ILogger<MasteringWorker> _logger;

    public MasteringWorker(IServiceScopeFactory scopes, ILogger<MasteringWorker> logger)
    {
        _scopes = scopes;
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

        try
        {
            if (engine.RequiresApproval)
                await RunPreviewEngineAsync(jobs, engine, storage, job, ct);
            else
                await RunOneShotEngineAsync(jobs, engine, storage, job, ct);
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(jobs, job, ex, ct);
        }
    }

    // ── ffmpeg (one-shot): master inline, upload WAV+MP3, status=done ──
    private async Task RunOneShotEngineAsync(
        IMasteringJobRepository jobs, IMasteringEngine engine, IObjectStorage storage,
        MasteringJob job, CancellationToken ct)
    {
        await FfmpegGate.WaitAsync(ct);
        try
        {
            using var source = await storage.OpenReadAsync(job.SourceKey)
                ?? throw new InvalidOperationException($"Source not found at {job.SourceKey}.");

            var result = await engine.MasterAsync(
                new MasteringEngineRequest
                {
                    Source = source.Stream,
                    SourceFileName = job.SourceFileName ?? "audio",
                    TargetLufs = job.TargetLufs,
                    TargetTruePeakDbtp = job.TargetTruePeakDbtp,
                },
                ct);

            await UploadMastersAsync(storage, job, result);
            job.InputLufs = result.InputLufs;
            job.OutputLufs = result.OutputLufs;
            job.OutputTruePeakDbtp = result.OutputTruePeakDbtp;
            job.Status = "done";
            job.CompletedAt = DateTime.UtcNow;
            job.Error = null;
            await jobs.UpdateAsync(job, ct);

            _logger.LogInformation(
                "EVENT: MasteringJobDone jobId:{JobId} engine:{Engine} wav:{Wav} mp3:{Mp3}",
                job.Id, engine.Name, job.MasteredWavKey is not null, job.MasteredMp3Key is not null);
        }
        finally
        {
            FfmpegGate.Release();
        }
    }

    // ── Tonn (preview): produce a preview, store it, status=awaiting_approval ──
    private async Task RunPreviewEngineAsync(
        IMasteringJobRepository jobs, IMasteringEngine engine, IObjectStorage storage,
        MasteringJob job, CancellationToken ct)
    {
        // RoEx fetches the source from a signed URL.
        var sourceUrl = storage.GenerateSignedUrl(job.SourceKey);

        var result = await engine.MasterAsync(
            new MasteringEngineRequest
            {
                SourceUrl = sourceUrl,
                SourceFileName = job.SourceFileName ?? "audio",
                TargetLufs = job.TargetLufs,
                TargetTruePeakDbtp = job.TargetTruePeakDbtp,
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
        await jobs.UpdateAsync(job, ct);

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
    private async Task HandleFailureAsync(IMasteringJobRepository jobs, MasteringJob job, Exception ex, CancellationToken ct)
    {
        if (job.RetryCount < 1)
        {
            job.RetryCount += 1;
            job.Status = "queued";
            job.StartedAt = null;
            job.Error = Truncate(ex.Message);
            await jobs.UpdateAsync(job, ct);
            _logger.LogWarning(ex, "EVENT: MasteringJobRetry jobId:{JobId} retryCount:{Retry}", job.Id, job.RetryCount);
            return;
        }

        job.Status = "failed";
        job.Error = Truncate(ex.Message);
        job.CompletedAt = DateTime.UtcNow;
        await jobs.UpdateAsync(job, ct);

        SentrySdk.CaptureException(ex);
        _logger.LogError(ex, "EVENT: MasteringJobFailed jobId:{JobId}", job.Id);
    }

    private static string Truncate(string s) => s.Length <= 1000 ? s : s[..1000];
}
