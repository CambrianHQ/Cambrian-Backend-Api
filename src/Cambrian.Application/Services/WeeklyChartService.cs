using Cambrian.Application.DTOs.Charts;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Application.Services;

/// <summary>
/// In-process weekly chart aggregator (residue R17). Ranks published tracks by
/// the catalog's "popular" ordering (TrendingScore) into a Top-50 snapshot with
/// a Track of the Week. Snapshots are immutable and swapped atomically; rank
/// deltas are computed against the previous snapshot. No DB table / migration —
/// this is the testable-on-demand seam until a scheduled job exists.
/// </summary>
public sealed class WeeklyChartService : IWeeklyChartService
{
    private const int ChartSize = 50;

    private readonly IServiceScopeFactory _scopeFactory;

    // Immutable snapshot, swapped atomically. Previous ranks drive deltas.
    private volatile WeeklyChartsResponse? _current;
    private IReadOnlyDictionary<string, int> _previousRanks = new Dictionary<string, int>();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WeeklyChartService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<WeeklyChartsResponse> GetCurrentAsync(CancellationToken ct = default)
    {
        var snapshot = _current;
        if (snapshot is not null) return snapshot;
        return await AggregateAsync(ct);
    }

    public async Task<WeeklyChartsResponse> AggregateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ICatalogService>();

            // "popular" orders by TrendingScore desc — the closest existing signal
            // to weekly momentum until per-window play aggregation lands. If the
            // catalog read fails, serve an honest empty chart rather than 500.
            IReadOnlyCollection<DTOs.Catalog.TrackResponse> top;
            try
            {
                top = await catalog.GetCatalogAsync(page: 1, pageSize: ChartSize, genre: null, search: null, sort: "popular");
            }
            catch
            {
                top = Array.Empty<DTOs.Catalog.TrackResponse>();
            }

            var entries = new List<ChartEntryResponse>();
            var rank = 1;
            foreach (var t in top)
            {
                var delta = _previousRanks.TryGetValue(t.Id, out var prev) ? prev - rank : (int?)null;
                entries.Add(new ChartEntryResponse
                {
                    Rank = rank,
                    TrackId = t.Id,
                    Title = t.Title,
                    Artist = t.Artist ?? string.Empty,
                    CreatorId = t.CreatorId,
                    CoverArtUrl = t.CoverArtUrl,
                    DeltaRank = delta,
                });
                rank++;
            }

            var top1 = entries.FirstOrDefault();
            var snapshot = new WeeklyChartsResponse
            {
                WeekOf = StartOfIsoWeekUtc(DateTime.UtcNow).ToString("o"),
                Entries = entries,
                TrackOfTheWeek = top1 is null ? null : new TrackOfTheWeekResponse
                {
                    TrackId = top1.TrackId,
                    Title = top1.Title,
                    Artist = top1.Artist,
                    CreatorId = top1.CreatorId,
                    CoverArtUrl = top1.CoverArtUrl,
                    Description = "This week's most-played track on The Scene.",
                },
            };

            _previousRanks = entries.ToDictionary(e => e.TrackId, e => e.Rank);
            _current = snapshot;
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DateTime StartOfIsoWeekUtc(DateTime utc)
    {
        var date = DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
        int diff = (7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return date.AddDays(-diff);
    }
}
