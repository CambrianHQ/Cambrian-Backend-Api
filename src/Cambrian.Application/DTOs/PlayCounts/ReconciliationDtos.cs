namespace Cambrian.Application.DTOs.PlayCounts;

/// <summary>Options for one reconciliation pass — see IPlayCountReconciliationService.</summary>
public sealed class ReconciliationOptions
{
    /// <summary>Null/empty = every track, paged in bounded batches. Non-empty = exactly these tracks.</summary>
    public IReadOnlyCollection<Guid>? TrackIds { get; init; }

    /// <summary>When true (the default), mismatches are found and recorded but nothing is written to TrackStats/CreatorStats.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>When true and DryRun is false, mismatched TrackStats/CreatorStats rows are corrected. Ignored (forced false) when DryRun is true.</summary>
    public bool Repair { get; init; }

    public int BatchSize { get; init; } = 500;

    /// <summary>Who/what triggered this run — an admin user id, or "system" for the scheduled worker.</summary>
    public string RequestedBy { get; init; } = "system";
}

/// <summary>One track whose stored counters didn't match what the durable events compute to.</summary>
public sealed record ReconciliationMismatch(
    Guid TrackId,
    long StoredPlayCount,
    long ComputedPlayCount,
    long StoredUniqueListenerCount,
    long ComputedUniqueListenerCount,
    bool Repaired);

/// <summary>Result of one reconciliation pass, mirroring the auditable PlayCountReconciliationRun row.</summary>
public sealed class ReconciliationRunResult
{
    public Guid RunId { get; init; }

    public bool DryRun { get; init; }

    public int TracksScanned { get; init; }

    public int MismatchesFound { get; init; }

    public int MismatchesRepaired { get; init; }

    /// <summary>"completed" | "failed" | "running" (only if interrupted mid-pass).</summary>
    public string Status { get; init; } = "completed";

    public IReadOnlyList<ReconciliationMismatch> Mismatches { get; init; } = Array.Empty<ReconciliationMismatch>();
}
