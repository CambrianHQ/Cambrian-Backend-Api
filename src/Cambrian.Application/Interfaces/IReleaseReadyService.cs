using Cambrian.Application.DTOs.ReleaseReady;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Orchestrates the Release Ready flow: upload+validate → submit (ffmpeg charges
/// here) → optional approve (Tonn charges here) → status → download. The
/// controller is a thin HTTP adapter over this service.
/// </summary>
public interface IReleaseReadyService
{
    Task<CreditStatusDto> GetCreditsAsync(string userId, CancellationToken ct = default);

    /// <summary>Store the source, run validation, persist DDEX AI-disclosure on the track
    /// (when a TrackId is supplied), and create a <c>validated</c> draft job. No credit spent.</summary>
    Task<ValidateResponse> ValidateAndCreateAsync(ReleaseReadyUploadInput input, CancellationToken ct = default);

    /// <summary>Start mastering. For one-shot engines (ffmpeg) this charges a credit
    /// (throws <c>InsufficientCreditsException</c> at zero) and queues the job. For
    /// preview engines (Tonn) it queues without charging (charge happens at approve).</summary>
    Task<JobDto> SubmitAsync(Guid jobId, string userId, CancellationToken ct = default);

    /// <summary>Approve a preview (preview engines only): charges a credit (throws at zero)
    /// and finalizes the master. Invalid for one-shot engines.</summary>
    Task<JobDto> ApproveAsync(Guid jobId, string userId, CancellationToken ct = default);

    Task<JobDto?> GetJobAsync(Guid jobId, string userId, CancellationToken ct = default);

    Task<IReadOnlyList<JobSummaryDto>> ListJobsAsync(string userId, int take, CancellationToken ct = default);

    /// <summary>Resolve a mastered-asset download (<paramref name="format"/> = wav|mp3).
    /// Returns null when the job/asset is absent or not owned.</summary>
    Task<MasteringDownload?> GetDownloadAsync(Guid jobId, string userId, string format, CancellationToken ct = default);
}
