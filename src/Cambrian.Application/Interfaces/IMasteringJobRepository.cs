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

    /// <summary>Count credit-charged, non-failed jobs since the month start (failed jobs release their credit).</summary>
    Task<int> CountChargedThisMonthAsync(string creatorId, DateTime monthStartUtc, CancellationToken ct = default);

    /// <summary>Race-safely claim the next queued job for the worker (sets status=processing, StartedAt).
    /// Returns null when the queue is empty.</summary>
    Task<MasteringJob?> ClaimNextQueuedAsync(CancellationToken ct = default);
}
