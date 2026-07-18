using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Admin;

/// <summary>
/// Bounds a play-ledger reconciliation read. When <see cref="TrackIds"/> is
/// omitted the operation covers the full catalog, while the returned mismatch
/// detail remains capped by <see cref="MismatchLimit"/>.
/// </summary>
public sealed class PlayReconciliationRequest : IValidatableObject
{
    public const int MaximumMismatchLimit = 500;
    public const int MaximumTrackSelection = 500;

    [Range(1, MaximumMismatchLimit)]
    public int MismatchLimit { get; init; } = 100;

    public IReadOnlyCollection<Guid>? TrackIds { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TrackIds is null)
            yield break;

        if (TrackIds.Count > MaximumTrackSelection)
        {
            yield return new ValidationResult(
                $"At most {MaximumTrackSelection} track IDs may be inspected at once.",
                [nameof(TrackIds)]);
        }

        if (TrackIds.Any(id => id == Guid.Empty))
        {
            yield return new ValidationResult(
                "Track IDs must be non-empty UUIDs.",
                [nameof(TrackIds)]);
        }
    }
}

/// <summary>
/// Bounds a repair by both the number of affected tracks and the number of
/// event rows whose aggregation marker may be updated in one transaction.
/// </summary>
public sealed class PlayReconciliationRepairRequest : IValidatableObject
{
    public const int MaximumTrackBatchSize = 100;
    public const int MaximumEventBatchSize = 5_000;
    public const int MaximumTrackSelection = 500;

    [Range(1, MaximumTrackBatchSize)]
    public int TrackBatchSize { get; init; } = 25;

    [Range(1, MaximumEventBatchSize)]
    public int EventBatchSize { get; init; } = 1_000;

    public IReadOnlyCollection<Guid>? TrackIds { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TrackIds is null)
            yield break;

        if (TrackIds.Count > MaximumTrackSelection)
        {
            yield return new ValidationResult(
                $"At most {MaximumTrackSelection} track IDs may be repaired at once.",
                [nameof(TrackIds)]);
        }

        if (TrackIds.Any(id => id == Guid.Empty))
        {
            yield return new ValidationResult(
                "Track IDs must be non-empty UUIDs.",
                [nameof(TrackIds)]);
        }
    }
}

/// <summary>
/// One disagreement between the append-only qualified-play ledger and its
/// synchronous lifetime projection.
/// </summary>
public sealed record PlayCountMismatch(
    Guid TrackId,
    long LegacyPlayCount,
    long LedgerQualifiedPlayCount,
    long StoredQualifiedPlayCount,
    long ExpectedLifetimePlayCount,
    long StoredLifetimePlayCount,
    long PendingAggregationCount,
    DateTime? LatestQualifiedAtUtc);

/// <summary>
/// Read-only reconciliation output. Counts honor an optional track filter;
/// chart freshness is always global because the Scene snapshot is global.
/// </summary>
public sealed record PlayReconciliationReport(
    DateTime CheckedAtUtc,
    int? SelectedTrackCount,
    long QualifiedEventCount,
    long StoredQualifiedPlayCount,
    long StoredLifetimePlayCount,
    int MismatchedTrackCount,
    IReadOnlyList<PlayCountMismatch> Mismatches,
    bool MismatchesTruncated,
    long DuplicateIdempotencyKeyGroupCount,
    long DuplicatePlaybackSessionGroupCount,
    long LegacyStreamSessionCount,
    long HistoricalSessionsWithoutReconstructableQualificationCount,
    long PendingAggregationCount,
    DateTime? OldestPendingQualifiedAtUtc,
    double? AggregationLagSeconds,
    DateTime? LatestChartWeekStartUtc,
    DateTime? LatestChartComputedAtUtc,
    DateTime? LatestChartDataThroughUtc,
    double? ChartDataAgeSeconds,
    int StaleChartWindowCount,
    int LegacyNonzeroTrendingScoreCount);

/// <summary>
/// Result of a bounded, transactional repair. A busy result means another
/// instance owns the PostgreSQL reconciliation advisory lock and no projection
/// rows were changed.
/// </summary>
public sealed record PlayReconciliationRepairResult(
    string Status,
    bool LockAcquired,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    int TrackBatchSize,
    int EventBatchSize,
    int CandidateTrackCount,
    int RepairedTrackCount,
    int EventsMarkedAggregated,
    IReadOnlyList<Guid> RepairedTrackIds,
    int RemainingMismatchedTrackCount,
    long RemainingPendingAggregationCount,
    DateTime? OldestPendingQualifiedAtUtc,
    double? AggregationLagSeconds);

/// <summary>Small health payload used by the protected health-details route.</summary>
public sealed record PlayPipelineHealthDetails(
    string Status,
    long QualifiedEventCount,
    long PendingAggregationCount,
    DateTime? OldestPendingQualifiedAtUtc,
    double? AggregationLagSeconds,
    DateTime? LatestChartComputedAtUtc,
    DateTime? LatestChartDataThroughUtc,
    double? ChartDataAgeSeconds,
    int StaleChartWindowCount,
    int LegacyNonzeroTrendingScoreCount);
