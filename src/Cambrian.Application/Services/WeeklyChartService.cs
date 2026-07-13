using Cambrian.Application.DTOs.Charts;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Application.Services;

/// <summary>
/// Weekly Scene chart — persisted, time-windowed, idempotent, deterministic.
///
/// THE single UTC chart window: <see cref="StartOfIsoWeekUtc"/> (Monday 00:00 UTC,
/// ISO week) through +7 days. Every chart read, write, and the scheduled worker
/// derive the window from this one method.
///
/// Ranking input is qualified plays: stream sessions started INSIDE the chart
/// week, on an eligible public track (see WeeklyChartRepository's eligibility
/// predicate) — every track with a qualified play that week is a candidate,
/// not a fixed-size slice of the catalog, so a track can never be excluded
/// from ranking just because it's not "new". While a week has no qualified
/// plays yet (bootstrap), the newest eligible tracks are shown instead and the
/// snapshot is marked Basis = "catalog_trending" so the frontend can label it
/// honestly — <see cref="Track.TrendingScore"/> is never read here: it is not
/// written by any production process (see ICatalogService's own doc comment)
/// and must not drive a live ranking.
///
/// Ordering is fully deterministic: score desc, qualified plays desc
/// (documented tie-break — today Score == qualified plays, but this clause
/// keeps ties resolved even if the score formula ever diverges), publish time
/// (Track.CreatedAt) desc, then track id — so re-running a recompute over
/// unchanged data always produces the same order and the same page contents.
///
/// Rank deltas come from the PREVIOUS week's persisted rows. Recompute
/// replaces the week's rows in one transaction — safe to run any number of
/// times (scheduled worker, admin trigger, or both).
/// </summary>
public sealed class WeeklyChartService : IWeeklyChartService
{
    private const int ChartSize = 50;
    private const int BootstrapPoolSize = 200;
    public const string BasisWeeklyPlays = "weekly_plays";
    public const string BasisCatalogTrending = "catalog_trending";

    /// <summary>
    /// Target recompute cadence — the scheduled worker (WeeklyChartWorker) runs
    /// at this interval, so a ranking is never more than this far behind
    /// authoritative play data (freshness target: rankings within 60 seconds).
    /// </summary>
    public static readonly TimeSpan RecomputeInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A snapshot older than this is reported <c>stale</c> to the frontend — a
    /// generous multiple of <see cref="RecomputeInterval"/> so ordinary tick
    /// jitter never trips it, while a genuinely stuck/crashed worker is caught
    /// within a few minutes instead of silently serving ancient data forever.
    /// </summary>
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromTicks(RecomputeInterval.Ticks * 3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WeeklyChartService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<WeeklyChartsResponse> GetCurrentAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWeeklyChartRepository>();

        var now = DateTime.UtcNow;
        var weekStart = StartOfIsoWeekUtc(now);
        var rows = await repo.GetWeekAsync(weekStart, ct);
        if (rows.Count > 0)
        {
            var stale = IsStale(rows[0].ComputedAtUtc, now);
            return ToResponse(rows, stale, await ResolveUsernamesAsync(repo, rows, ct));
        }

        // No snapshot for the running week yet — serve the latest persisted
        // week as a stand-in rather than an empty chart (the worker will catch
        // up). This is ALWAYS stale: it is a previous week's data being shown
        // in place of this week's, not merely a slow refresh of this week's.
        var latest = await repo.GetLatestWeekAsync(ct);
        if (latest.Count > 0)
            return ToResponse(latest, stale: true, await ResolveUsernamesAsync(repo, latest, ct));

        // Nothing persisted at all (fresh deploy) — compute once, persisted, fresh.
        return await AggregateAsync(ct);
    }

    public async Task<ChartArchiveIndexResponse> GetArchiveIndexAsync(int limit = 104, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWeeklyChartRepository>();

        var currentWeekStart = StartOfIsoWeekUtc(DateTime.UtcNow);
        var completedWeeks = (await repo.ListWeekStartsAsync(limit + 1, ct))
            .Where(w => w < currentWeekStart)
            .Take(limit)
            .ToList();

        var topRows = await repo.GetTopRowsForWeeksAsync(completedWeeks, ct);
        var topByWeek = topRows.ToDictionary(r => r.WeekStartUtc, r => r);

        var weeks = completedWeeks
            .Select(weekStart =>
            {
                topByWeek.TryGetValue(weekStart, out var top);
                return new ChartArchiveWeekSummary
                {
                    IsoWeek = ToIsoWeekKey(weekStart),
                    WeekOf = weekStart.ToString("o"),
                    WeekEnd = weekStart.AddDays(7).ToString("o"),
                    // The index only carries the headline row; entry count is
                    // resolved on the week page itself (always ≤ ChartSize).
                    Entries = top is null ? 0 : ChartSize,
                    TopTrackId = top?.TrackId.ToString(),
                    TopTrackTitle = top?.Title,
                    TopTrackArtist = top?.Artist,
                };
            })
            .ToList();

        return new ChartArchiveIndexResponse { Weeks = weeks };
    }

    public async Task<WeeklyChartsResponse?> GetArchivedWeekAsync(DateTime weekStartUtc, CancellationToken ct = default)
    {
        // The running (and any future) week is never archived — it lives on
        // /scene and still changes. This keeps archive URLs permanent records.
        var normalized = StartOfIsoWeekUtc(weekStartUtc);
        if (normalized >= StartOfIsoWeekUtc(DateTime.UtcNow)) return null;

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWeeklyChartRepository>();

        var rows = await repo.GetWeekAsync(normalized, ct);
        if (rows.Count == 0) return null;
        // A completed week is a permanent record — never reported stale.
        return ToResponse(rows, stale: false, await ResolveUsernamesAsync(repo, rows, ct));
    }

    /// <summary>
    /// THE single definition of a Scene chart week: UTC Monday 00:00 (ISO
    /// week). Every chart endpoint, the ranking query, and the scheduled
    /// worker derive their window from this method — there is exactly one
    /// place that decides what "this week" means for charts.
    /// </summary>
    public static DateTime StartOfIsoWeekUtc(DateTime utc)
    {
        var date = DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
        int diff = (7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return date.AddDays(-diff);
    }

    /// <summary>"2026-w28" — the URL key for a chart week's archive page.</summary>
    public static string ToIsoWeekKey(DateTime weekStartUtc)
    {
        var year = System.Globalization.ISOWeek.GetYear(weekStartUtc);
        var week = System.Globalization.ISOWeek.GetWeekOfYear(weekStartUtc);
        return $"{year}-w{week:D2}";
    }

    /// <summary>Parse "2026-w28" / "2026-W28" to the week's Monday 00:00 UTC; null on garbage.</summary>
    public static DateTime? ParseIsoWeekKey(string? isoWeek)
    {
        if (string.IsNullOrWhiteSpace(isoWeek)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(isoWeek.Trim(), @"^(\d{4})-[wW](\d{1,2})$");
        if (!match.Success) return null;
        var year = int.Parse(match.Groups[1].Value);
        var week = int.Parse(match.Groups[2].Value);
        if (week < 1 || week > 53) return null;
        try
        {
            var monday = System.Globalization.ISOWeek.ToDateTime(year, week, DayOfWeek.Monday);
            return DateTime.SpecifyKind(monday, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // e.g. week 53 of a 52-week year
        }
    }

    /// <summary>
    /// Whether a snapshot computed at <paramref name="computedAtUtc"/> counts as
    /// stale when observed at <paramref name="nowUtc"/>. A pure function of two
    /// explicit timestamps (rather than reading the clock itself) so freshness
    /// boundary behavior is deterministically unit-testable.
    /// </summary>
    public static bool IsStale(DateTime computedAtUtc, DateTime nowUtc) =>
        nowUtc - computedAtUtc > StaleThreshold;

    private static async Task<IReadOnlyDictionary<string, string>> ResolveUsernamesAsync(
        IWeeklyChartRepository repo,
        IReadOnlyList<WeeklyChartSnapshot> rows,
        CancellationToken ct)
    {
        var ids = rows.Select(r => r.CreatorId).Where(id => id.Length > 0).Distinct().ToList();
        return await repo.GetUsernamesByUserIdsAsync(ids, ct);
    }

    public async Task<WeeklyChartsResponse> AggregateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IWeeklyChartRepository>();

            var weekStart = StartOfIsoWeekUtc(DateTime.UtcNow);
            var weekEnd = weekStart.AddDays(7);

            // Previous week's persisted ranks drive movement deltas.
            var previousWeekRows = await repo.GetWeekAsync(weekStart.AddDays(-7), ct);
            var previousRanks = previousWeekRows.ToDictionary(r => r.TrackId, r => r.Rank);

            // Authoritative ranking input: every eligible track with a qualified
            // play this week, sourced directly from StreamSessions — never
            // narrowed to an arbitrary catalog slice, so a track can't be
            // excluded from ranking just because it wasn't already "popular".
            var qualifiedPlays = await repo.GetQualifiedPlayCountsInWindowAsync(weekStart, weekEnd, ct);

            string basis;
            IReadOnlyList<Track> candidates;
            if (qualifiedPlays.Count > 0)
            {
                basis = BasisWeeklyPlays;
                candidates = await repo.GetEligibleTracksByIdsAsync(qualifiedPlays.Keys.ToList(), ct);
            }
            else
            {
                // Bootstrap: no qualified plays anywhere yet this week. Fall
                // back to the newest eligible tracks — an honest, real signal
                // (not the dead Track.TrendingScore column) — until real plays
                // land. Still clearly labeled so the frontend never claims
                // this is a plays-based ranking.
                basis = BasisCatalogTrending;
                candidates = await repo.GetNewestEligibleTracksAsync(BootstrapPoolSize, ct);
            }

            // Deterministic ranking: score desc, qualified plays desc (documented
            // tie-break — today Score == qualified plays), publish time desc,
            // then track id as the final, unconditional tiebreaker.
            var ranked = candidates
                .Select(t =>
                {
                    var plays = qualifiedPlays.TryGetValue(t.Id, out var p) ? p : 0;
                    return new
                    {
                        Track = t,
                        QualifiedPlays = plays,
                        Score = (double)plays, // Score == qualified plays today; kept as its own field for a future weighted formula.
                    };
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.QualifiedPlays)
                .ThenByDescending(x => x.Track.CreatedAt)
                .ThenBy(x => x.Track.Id)
                .Take(ChartSize)
                .ToList();

            var lifetimePlays = await repo.GetLifetimePlayCountsAsync(ranked.Select(x => x.Track.Id).ToList(), ct);

            var computedAt = DateTime.UtcNow;
            var rows = new List<WeeklyChartSnapshot>(ranked.Count);
            var rank = 1;
            foreach (var candidate in ranked)
            {
                var track = candidate.Track;
                var previousRank = previousRanks.TryGetValue(track.Id, out var prev) ? prev : (int?)null;
                rows.Add(new WeeklyChartSnapshot
                {
                    Id = Guid.NewGuid(),
                    WeekStartUtc = weekStart,
                    WeekEndUtc = weekEnd,
                    Rank = rank,
                    PreviousRank = previousRank,
                    DeltaRank = previousRank is int p2 ? p2 - rank : null,
                    TrackId = track.Id,
                    CreatorId = track.CreatorId,
                    Title = track.Title,
                    Artist = CatalogService.ResolveArtistName(track),
                    CoverArtUrl = track.CoverArtUrl,
                    Score = candidate.Score,
                    PlaysInWindow = candidate.QualifiedPlays,
                    LifetimePlays = lifetimePlays.TryGetValue(track.Id, out var lp) ? lp : 0,
                    Basis = basis,
                    ComputedAtUtc = computedAt,
                });
                rank++;
            }

            await repo.ReplaceWeekAsync(weekStart, rows, ct);
            // Just computed — always fresh.
            return ToResponse(rows, stale: false, await ResolveUsernamesAsync(repo, rows, ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static WeeklyChartsResponse ToResponse(
        IReadOnlyList<WeeklyChartSnapshot> rows,
        bool stale,
        IReadOnlyDictionary<string, string>? usernames = null)
    {
        var entries = rows
            .OrderBy(r => r.Rank)
            .Select(r => new ChartEntryResponse
            {
                Rank = r.Rank,
                TrackId = r.TrackId.ToString(),
                Title = r.Title,
                Artist = r.Artist,
                CreatorId = r.CreatorId,
                CreatorUsername = usernames != null && usernames.TryGetValue(r.CreatorId, out var u) ? u : null,
                CoverArtUrl = r.CoverArtUrl,
                DeltaRank = r.DeltaRank,
                PlaysInWindow = r.PlaysInWindow,
                LifetimePlays = r.LifetimePlays,
                Score = r.Score,
            })
            .ToList();

        var top1 = rows.OrderBy(r => r.Rank).FirstOrDefault();
        // An EMPTY chart must not claim it was ranked by weekly plays — default
        // the basis to the honest bootstrap label when there are no rows.
        var basis = top1?.Basis ?? BasisCatalogTrending;
        var weekStart = top1?.WeekStartUtc ?? StartOfIsoWeekUtc(DateTime.UtcNow);
        var weekEnd = top1?.WeekEndUtc ?? weekStart.AddDays(7);
        // The window had already closed by GeneratedAt (archived week) → data
        // runs through WeekEnd. Otherwise (running week) the window is still
        // open → data runs through GeneratedAt itself.
        var dataThrough = top1 is null ? (DateTime?)null : (top1.ComputedAtUtc < top1.WeekEndUtc ? top1.ComputedAtUtc : top1.WeekEndUtc);

        return new WeeklyChartsResponse
        {
            WeekOf = weekStart.ToString("o"),
            WeekEnd = weekEnd.ToString("o"),
            Entries = entries,
            Basis = basis,
            GeneratedAt = top1?.ComputedAtUtc.ToString("o"),
            DataThrough = dataThrough?.ToString("o"),
            Stale = stale,
            TrackOfTheWeek = top1 is null ? null : new TrackOfTheWeekResponse
            {
                TrackId = top1.TrackId.ToString(),
                Title = top1.Title,
                Artist = top1.Artist,
                CreatorId = top1.CreatorId,
                CoverArtUrl = top1.CoverArtUrl,
                Description = basis == BasisWeeklyPlays
                    ? "This week's most-played track on The Scene."
                    : "Top of the catalog while this week's chart is forming.",
            },
        };
    }
}
