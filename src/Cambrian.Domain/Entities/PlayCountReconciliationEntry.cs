namespace Cambrian.Domain.Entities;

/// <summary>
/// One track-level mismatch found during a reconciliation run. Only mismatches are recorded
/// (not every track scanned) so this table stays bounded on a healthy, drift-free catalog.
/// </summary>
public class PlayCountReconciliationEntry
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public PlayCountReconciliationRun Run { get; set; } = null!;

    public Guid TrackId { get; set; }

    public long StoredPlayCount { get; set; }

    public long ComputedPlayCount { get; set; }

    public long StoredUniqueListenerCount { get; set; }

    public long ComputedUniqueListenerCount { get; set; }

    public bool Repaired { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
