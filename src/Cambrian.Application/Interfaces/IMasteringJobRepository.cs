using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

/// <summary>Data access for <see cref="MasteringJob"/> (repository-pattern governance).</summary>
public interface IMasteringJobRepository
{
    Task<MasteringJob?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Owner-scoped fetch (returns null when the job is absent or not owned).</summary>
    Task<MasteringJob?> GetForOwnerAsync(Guid id, string creatorId, CancellationToken ct = default);

    Task AddAsync(MasteringJob job, CancellationToken ct = default);

    Task UpdateAsync(MasteringJob job, CancellationToken ct = default);

    Task<IReadOnlyList<MasteringJob>> ListByCreatorAsync(string creatorId, int take, CancellationToken ct = default);

    /// <summary>Count MONTHLY credit-charged, non-failed jobs since the month start (failed jobs release their credit; purchased-funded charges excluded).</summary>
    Task<int> CountChargedThisMonthAsync(string creatorId, DateTime monthStartUtc, CancellationToken ct = default);

    /// <summary>Count non-failed jobs funded from the purchased credit pool (all time) — for deriving the remaining purchased balance.</summary>
    Task<int> CountPurchasedConsumedAsync(string creatorId, CancellationToken ct = default);

    /// <summary>Race-safely claim the next queued or expired processing job for the worker.
    /// Returns null when no claimable work remains.</summary>
    Task<MasteringJob?> ClaimNextQueuedAsync(CancellationToken ct = default);

    /// <summary>Extend the active processing lease. False means the lease is no longer current.</summary>
    Task<bool> HeartbeatAsync(Guid jobId, Guid leaseId, CancellationToken ct = default);

    /// <summary>Persist non-terminal worker progress only when the lease is still active.</summary>
    Task<bool> UpdateLeaseOwnedAsync(MasteringJob job, Guid leaseId, CancellationToken ct = default);

    /// <summary>Move a lease-owned processing job to awaiting approval.</summary>
    Task<bool> MarkAwaitingApprovalAsync(MasteringJob job, Guid leaseId, CancellationToken ct = default);

    /// <summary>Move a lease-owned processing job to done.</summary>
    Task<bool> MarkDoneAsync(MasteringJob job, Guid leaseId, CancellationToken ct = default);

    /// <summary>Requeue a failed processing attempt while preserving retry accounting.</summary>
    Task<bool> RequeueForRetryAsync(Guid jobId, Guid leaseId, int retryCount, string error, CancellationToken ct = default);

    /// <summary>Move a lease-owned processing job to failed.</summary>
    Task<bool> MarkFailedAsync(Guid jobId, Guid leaseId, string error, CancellationToken ct = default);

    /// <summary>Most recent job for a catalog track (any status). Null when none exists.</summary>
    Task<MasteringJob?> GetLatestForTrackAsync(Guid trackId, CancellationToken ct = default);

    /// <summary>
    /// Most recent non-failed job for (track, content hash) — the release-pipeline
    /// idempotency probe: an existing live job for unchanged audio means "warn,
    /// don't re-charge". Null when no such job exists.
    /// </summary>
    Task<MasteringJob?> GetActiveByTrackAndHashAsync(Guid trackId, string contentHash, CancellationToken ct = default);

    /// <summary>Most recent non-failed classic upload job for the same creator/content hash.</summary>
    Task<MasteringJob?> GetActiveClassicByCreatorAndHashAsync(string creatorId, string contentHash, CancellationToken ct = default);
}
