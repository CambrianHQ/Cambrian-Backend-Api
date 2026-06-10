using Cambrian.Application.DTOs.Readiness;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Track-based Release Ready orchestration (contract: POST /api/tracks/{id}/release-ready,
/// GET /api/jobs/{id}). Debits one monthly credit atomically, creates a
/// <see cref="MasteringJob"/> with <c>Kind=release_pipeline</c> over the track's stored
/// audio, and enqueues it for the in-process worker. After mastering, the worker calls
/// <see cref="RunPostMasteringStagesAsync"/> to run the Metadata → Cover → Disclosure →
/// Provenance stages, persisting each transition.
/// </summary>
public interface ITrackReleasePipelineService
{
    /// <summary>
    /// Start (or idempotently coalesce) a release job for the track. Throws
    /// <see cref="KeyNotFoundException"/> when the track is absent or not owned,
    /// <see cref="InvalidOperationException"/> when it has no stored audio, and
    /// <see cref="Cambrian.Application.Exceptions.InsufficientCreditsException"/>
    /// when the monthly allowance is exhausted.
    /// </summary>
    Task<StartReleaseJobResult> StartAsync(Guid trackId, string userId, CancellationToken ct = default);

    /// <summary>Owner-scoped job status with persisted stages + signed artifact URLs. Null when absent/not owned.</summary>
    Task<ReleaseJobResponse?> GetJobAsync(Guid jobId, string userId, CancellationToken ct = default);

    /// <summary>
    /// Run the post-mastering stages for a release-pipeline job. Mutates the job's
    /// Stage/StageHistoryJson (persisting transitions) but leaves final status to the
    /// caller. Throws on hard stage failure — the worker's failure path then marks the
    /// job failed, which releases the consumed credit.
    /// </summary>
    Task RunPostMasteringStagesAsync(MasteringJob job, CancellationToken ct = default);
}
