using Cambrian.Application.DTOs.PlayCounts;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Compares the TrackStats/CreatorStats projection against durable, qualified StreamSessions —
/// the only source of truth — and (optionally) repairs mismatches. Every run is recorded
/// (PlayCountReconciliationRun/Entry) whether or not it repairs anything, so drift and its
/// resolution are always auditable. Safe to run concurrently with itself or with the normal
/// write path: repair recomputes each row from scratch rather than applying a delta, so
/// re-running (after an interruption, or just on a schedule) always converges on the same
/// correct answer instead of compounding.
/// </summary>
public interface IPlayCountReconciliationService
{
    Task<ReconciliationRunResult> ReconcileAsync(ReconciliationOptions options, CancellationToken ct = default);
}
