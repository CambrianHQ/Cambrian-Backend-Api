namespace Cambrian.Domain.Entities;

/// <summary>
/// Auditable record of one reconciliation pass: what was compared, whether it was a dry run,
/// whether mismatches were repaired, and how far the pass got. A row is written when the run
/// starts and updated as bounded batches complete, so an interrupted run (process restart, worker
/// cancellation) still leaves a truthful partial record rather than silently vanishing.
/// </summary>
public class PlayCountReconciliationRun
{
    public Guid Id { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public bool DryRun { get; set; }

    public bool RepairRequested { get; set; }

    /// <summary>System, an admin user id, or an email — whoever/whatever triggered this run.</summary>
    public string RequestedBy { get; set; } = "system";

    /// <summary>"all" or a description of the requested track scope (e.g. "12 tracks").</summary>
    public string Scope { get; set; } = "all";

    public int BatchSize { get; set; }

    public int TracksScanned { get; set; }

    public int MismatchesFound { get; set; }

    public int MismatchesRepaired { get; set; }

    /// <summary>"running" | "completed" | "failed".</summary>
    public string Status { get; set; } = "running";

    public string? ErrorMessage { get; set; }

    public List<PlayCountReconciliationEntry> Entries { get; set; } = new();
}
