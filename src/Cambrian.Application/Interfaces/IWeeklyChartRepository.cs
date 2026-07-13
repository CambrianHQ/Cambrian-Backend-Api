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
    /// Qualified-play counts per track for stream sessions started in
    /// [fromUtc, toUtc) — eligible tracks only (see WeeklyChartRepository's
    /// eligibility predicate). This is the authoritative ranking input: every
    /// track with at least one qualified play in the window is a candidate,
    /// not just a fixed-size slice of the catalog.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetQualifiedPlayCountsInWindowAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>
    /// The eligible tracks matching the given ids, with Creator/CreatorEntity
    /// navigations loaded (so title/artist/cover can be read off the entity).
    /// Ineligible or deleted tracks among the ids are silently omitted.
    /// </summary>
    Task<IReadOnlyList<Track>> GetEligibleTracksByIdsAsync(IReadOnlyCollection<Guid> trackIds, CancellationToken ct = default);

    /// <summary>
    /// The newest eligible tracks (by CreatedAt, a real and honest signal) —
    /// used only as the bootstrap candidate pool while a chart week has zero
    /// qualified plays anywhere. Never used once real plays exist.
    /// </summary>
    Task<IReadOnlyList<Track>> GetNewestEligibleTracksAsync(int take, CancellationToken ct = default);

    /// <summary>
    /// All-time (lifetime) stream-session counts for the given tracks,
    /// regardless of current eligibility (a track's history doesn't vanish
    /// if it's later hidden). Display-only — never used for ranking.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, long>> GetLifetimePlayCountsAsync(IReadOnlyCollection<Guid> trackIds, CancellationToken ct = default);

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
