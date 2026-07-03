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

        if (rows.Count > 0) return ToResponse(rows);

        // Nothing persisted at all (fresh deploy) — compute once, persisted.
        return await AggregateAsync(ct);
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
            return ToResponse(rows);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static WeeklyChartsResponse ToResponse(IReadOnlyList<WeeklyChartSnapshot> rows)
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
                CoverArtUrl = r.CoverArtUrl,
                DeltaRank = r.DeltaRank,
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
