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
    /// Stream-session counts per public track started within [fromUtc, toUtc).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetTrackPlaysInWindowAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>
    /// Replace the given week's rows atomically (delete + insert in one
    /// transaction) — this is what makes recompute idempotent per week.
    /// </summary>
    Task ReplaceWeekAsync(DateTime weekStartUtc, IReadOnlyList<WeeklyChartSnapshot> rows, CancellationToken ct = default);

    /// <summary>Distinct persisted week starts, newest first, capped at <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<DateTime>> ListWeekStartsAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Rank-1 row per given week start (weeks with no rows are omitted) —
    /// powers the archive index without loading every week's full 50.
    /// </summary>
    Task<IReadOnlyList<WeeklyChartSnapshot>> GetTopRowsForWeeksAsync(IReadOnlyCollection<DateTime> weekStartsUtc, CancellationToken ct = default);

    /// <summary>
    /// Routable creator usernames for the given legacy user ids (missing
    /// identities omitted). Single query; used to enrich chart responses at
    /// read time so artist links survive renames.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetUsernamesByUserIdsAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default);
}
