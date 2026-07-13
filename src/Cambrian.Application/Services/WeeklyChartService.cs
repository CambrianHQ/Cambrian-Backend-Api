using Cambrian.Application.DTOs.Charts;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Application.Services;

/// <summary>
/// Weekly Scene chart — persisted, time-windowed, idempotent.
///
/// Ranking input is stream sessions started INSIDE the chart week (Monday
/// 00:00 UTC → +7d), so all-time popularity cannot dominate. While a week has
/// no plays yet (bootstrap), the catalog's trending order is used and the
/// snapshot is marked Basis = "catalog_trending" so the frontend can label it
/// honestly. Rank deltas come from the PREVIOUS week's persisted rows.
/// Recompute replaces the week's rows in one transaction — safe to run any
/// number of times (scheduled worker, admin trigger, or both).
/// </summary>
public sealed class WeeklyChartService : IWeeklyChartService
{
    private const int ChartSize = 50;
    public const string BasisWeeklyPlays = "weekly_plays";
    public const string BasisCatalogTrending = "catalog_trending";

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

        var weekStart = StartOfIsoWeekUtc(DateTime.UtcNow);
        var rows = await repo.GetWeekAsync(weekStart, ct);
        if (rows.Count == 0)
        {
            // No snapshot for the running week yet — serve the latest persisted
            // week rather than an empty chart (the worker will catch up).
            rows = await repo.GetLatestWeekAsync(ct);
        }

        if (rows.Count > 0) return ToResponse(rows, await ResolveUsernamesAsync(repo, rows, ct));

        // Nothing persisted at all (fresh deploy) — compute once, persisted.
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
        return ToResponse(rows, await ResolveUsernamesAsync(repo, rows, ct));
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
            var catalog = scope.ServiceProvider.GetRequiredService<ICatalogService>();

            var now = DateTime.UtcNow;
            var weekStart = StartOfIsoWeekUtc(now);
            var weekEnd = weekStart.AddDays(7);

            // Previous week's persisted ranks drive movement deltas.
            var previousWeekRows = await repo.GetWeekAsync(weekStart.AddDays(-7), ct);
            var previousRanks = previousWeekRows.ToDictionary(r => r.TrackId, r => r.Rank);

            // The catalog list supplies title/artist/cover + the bootstrap order.
            IReadOnlyCollection<DTOs.Catalog.TrackResponse> catalogTop;
            try
            {
                catalogTop = await catalog.GetCatalogAsync(page: 1, pageSize: 200, genre: null, search: null, sort: "popular");
            }
            catch
            {
                catalogTop = Array.Empty<DTOs.Catalog.TrackResponse>();
            }

            var playsByTrack = await repo.GetTrackPlaysInWindowAsync(weekStart, weekEnd, ct);
            var basis = playsByTrack.Count > 0 ? BasisWeeklyPlays : BasisCatalogTrending;

            // Score: in-window plays are the metric; the catalog's trending order
            // is only a stable tiebreak (and the whole order during bootstrap).
            var catalogIndex = catalogTop
                .Select((t, i) => (t, i))
                .ToDictionary(x => x.t.Id, x => x.i);

            var ranked = catalogTop
                .Select(t =>
                {
                    Guid.TryParse(t.Id, out var trackGuid);
                    var plays = playsByTrack.TryGetValue(trackGuid, out var p) ? p : 0;
                    return (Track: t, TrackGuid: trackGuid, Plays: plays);
                })
                .Where(x => x.TrackGuid != Guid.Empty)
                .OrderByDescending(x => x.Plays)
                .ThenBy(x => catalogIndex.TryGetValue(x.Track.Id, out var i) ? i : int.MaxValue)
                .Take(ChartSize)
                .ToList();

            var computedAt = DateTime.UtcNow;
            var rows = new List<WeeklyChartSnapshot>(ranked.Count);
            var rank = 1;
            foreach (var (track, trackGuid, plays) in ranked)
            {
                var previousRank = previousRanks.TryGetValue(trackGuid, out var prev) ? prev : (int?)null;
                rows.Add(new WeeklyChartSnapshot
                {
                    Id = Guid.NewGuid(),
                    WeekStartUtc = weekStart,
                    WeekEndUtc = weekEnd,
                    Rank = rank,
                    PreviousRank = previousRank,
                    DeltaRank = previousRank is int p2 ? p2 - rank : null,
                    TrackId = trackGuid,
                    CreatorId = track.CreatorId,
                    Title = track.Title,
                    Artist = track.Artist ?? string.Empty,
                    CoverArtUrl = track.CoverArtUrl,
                    Score = plays,
                    PlaysInWindow = plays,
                    Basis = basis,
                    ComputedAtUtc = computedAt,
                });
                rank++;
            }

            await repo.ReplaceWeekAsync(weekStart, rows, ct);
            return ToResponse(rows, await ResolveUsernamesAsync(repo, rows, ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static WeeklyChartsResponse ToResponse(
        IReadOnlyList<WeeklyChartSnapshot> rows,
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
            })
            .ToList();

        var top1 = rows.OrderBy(r => r.Rank).FirstOrDefault();
        // An EMPTY chart must not claim it was ranked by weekly plays — default
        // the basis to the honest bootstrap label when there are no rows.
        var basis = top1?.Basis ?? BasisCatalogTrending;

        return new WeeklyChartsResponse
        {
            WeekOf = (top1?.WeekStartUtc ?? StartOfIsoWeekUtc(DateTime.UtcNow)).ToString("o"),
            Entries = entries,
            Basis = basis,
            ComputedAt = top1?.ComputedAtUtc.ToString("o"),
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

    private static DateTime StartOfIsoWeekUtc(DateTime utc)
    {
        var date = DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
        int diff = (7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return date.AddDays(-diff);
    }
}
