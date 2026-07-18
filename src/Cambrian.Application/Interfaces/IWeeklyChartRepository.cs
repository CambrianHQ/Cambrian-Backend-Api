using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Persistence seam for weekly Scene chart snapshots (see WeeklyChartService).
/// </summary>
public interface IWeeklyChartRepository
{
    /// <summary>All rank rows for the given chart week, ordered by rank.</summary>
    Task<IReadOnlyList<WeeklyChartSnapshot>> GetWeekAsync(DateTime weekStartUtc, CancellationToken ct = default);

    /// <summary>Rank rows of the most recent persisted week, ordered by rank (empty when none).</summary>
    Task<IReadOnlyList<WeeklyChartSnapshot>> GetLatestWeekAsync(CancellationToken ct = default);

    /// <summary>
    /// Qualified-play counts per eligible public track inside [fromUtc, toUtc).
    /// Kept for callers that only need the grouped counts; chart aggregation should
    /// use <see cref="GetEligibleCandidatesAsync"/> so eligibility and lifetime
    /// counts come from the same repository boundary.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetTrackPlaysInWindowAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>
    /// All tracks eligible for the public Scene chart, with qualified weekly and
    /// authoritative lifetime play counts. <paramref name="dataThroughUtc"/> is an
    /// inclusive event-time cutoff captured before the query begins.
    /// </summary>
    Task<IReadOnlyList<WeeklyChartCandidate>> GetEligibleCandidatesAsync(
        DateTime fromUtc,
        DateTime toUtc,
        DateTime dataThroughUtc,
        CancellationToken ct = default);

    /// <summary>
    /// True when a qualified play in the chart window is newer than the snapshot's
    /// data-through watermark. The observed-at bound prevents future-dated rows from
    /// keeping the chart permanently behind.
    /// </summary>
    Task<bool> HasQualifiedPlaysAfterAsync(
        DateTime fromUtc,
        DateTime toUtc,
        DateTime dataThroughUtc,
        DateTime observedAtUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Replace the given week's rows atomically (delete + insert in one
    /// transaction) — this is what makes recompute idempotent per week.
    /// </summary>
    Task ReplaceWeekAsync(DateTime weekStartUtc, IReadOnlyList<WeeklyChartSnapshot> rows, CancellationToken ct = default);
}

/// <summary>
/// Persistence-neutral ranking input for one eligible Scene track.
/// </summary>
public sealed record WeeklyChartCandidate(
    Guid TrackId,
    string CreatorId,
    string Title,
    string Artist,
    string? CoverArtUrl,
    DateTime CreatedAtUtc,
    long WeeklyQualifiedPlays,
    long LifetimePlays);
