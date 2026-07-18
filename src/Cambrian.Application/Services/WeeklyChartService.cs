using Cambrian.Application.DTOs.Charts;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Observability;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

/// <summary>
/// Persisted weekly Scene chart ranked only by qualified play events inside the
/// current Monday-UTC half-open window. Every eligible public/playable track is
/// considered, including tracks with zero weekly plays. Snapshot replacement is
/// delegated to the repository's transactional, cross-instance-safe boundary.
/// </summary>
public sealed class WeeklyChartService : IWeeklyChartService
{
    private const int ChartSize = 50;
    private const int DefaultStaleAfterSeconds = 60;

    /// <summary>
    /// Backward-compatible basis value. Its current semantics are specifically
    /// qualified plays inside the chart window, never raw sessions or catalog score.
    /// </summary>
    public const string BasisWeeklyPlays = "weekly_plays";

    /// <summary>Retained for source compatibility; new snapshots never use it.</summary>
    [Obsolete("The Scene chart no longer falls back to catalog popularity.")]
    public const string BasisCatalogTrending = "catalog_trending";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WeeklyChartService> _logger;
    private readonly TimeSpan _staleAfter;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WeeklyChartService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IConfiguration configuration,
        ILogger<WeeklyChartService> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;

        var configuredSeconds = configuration.GetValue<int?>("Charts:Weekly:StaleAfterSeconds")
            ?? DefaultStaleAfterSeconds;
        _staleAfter = TimeSpan.FromSeconds(Math.Max(1, configuredSeconds));
    }

    public async Task<WeeklyChartsResponse> GetCurrentAsync(CancellationToken ct = default)
    {
        var observedAt = UtcNow();
        var weekStart = StartOfIsoWeekUtc(observedAt);
        var weekEnd = weekStart.AddDays(7);

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWeeklyChartRepository>();
        var rows = await repo.GetWeekAsync(weekStart, ct);

        // Never substitute the previous week for a missing current-week chart.
        // A fresh deployment calculates the exact requested week or fails visibly.
        if (rows.Count == 0)
        {
            return await AggregateAsync(ct);
        }

        try
        {
            var generatedAt = rows.Max(row => row.ComputedAtUtc);
            var dataThrough = rows.Max(row => row.DataThroughUtc);
            var staleByAge = generatedAt < observedAt.Subtract(_staleAfter);
            var behind = dataThrough is null
                || await repo.HasQualifiedPlaysAfterAsync(
                    weekStart,
                    weekEnd,
                    dataThrough.Value,
                    observedAt,
                    ct);

            if (!staleByAge && !behind)
            {
                return ToResponse(rows, weekStart, observedAt, forceStale: false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "EVENT: WeeklyChartFreshnessCheckFailed weekStart:{WeekStart}",
                weekStart);
            return ToResponse(rows, weekStart, observedAt, forceStale: true);
        }

        try
        {
            return await AggregateAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Aggregation reads all inputs before transactional replacement. If an
            // input query fails, the prior current-week snapshot remains intact.
            _logger.LogWarning(
                ex,
                "EVENT: WeeklyChartLazyRefreshFailed weekStart:{WeekStart}",
                weekStart);
            return ToResponse(rows, weekStart, observedAt, forceStale: true);
        }
    }

    public async Task<WeeklyChartsResponse> AggregateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IWeeklyChartRepository>();

            // Capture the inclusive event-time watermark before querying. Events
            // after it are intentionally left for the next lazy/worker refresh.
            var dataThrough = UtcNow();
            var weekStart = StartOfIsoWeekUtc(dataThrough);
            var weekEnd = weekStart.AddDays(7);

            var previousWeekRows = await repo.GetWeekAsync(weekStart.AddDays(-7), ct);
            var previousRanks = previousWeekRows
                .Where(row => row.Rank > 0)
                .GroupBy(row => row.TrackId)
                .ToDictionary(group => group.Key, group => group.First().Rank);

            var candidates = await repo.GetEligibleCandidatesAsync(
                weekStart,
                weekEnd,
                dataThrough,
                ct);

            var ranked = candidates
                .OrderByDescending(candidate => candidate.WeeklyQualifiedPlays)
                .ThenByDescending(candidate => candidate.CreatedAtUtc)
                .ThenBy(candidate => candidate.TrackId)
                .Take(ChartSize)
                .ToList();

            var generatedAt = UtcNow();
            var rows = new List<WeeklyChartSnapshot>(Math.Max(1, ranked.Count));
            var rank = 1;
            foreach (var candidate in ranked)
            {
                var previousRank = previousRanks.GetValueOrDefault(candidate.TrackId);
                rows.Add(new WeeklyChartSnapshot
                {
                    Id = Guid.NewGuid(),
                    WeekStartUtc = weekStart,
                    WeekEndUtc = weekEnd,
                    Rank = rank,
                    PreviousRank = previousRank == 0 ? null : previousRank,
                    DeltaRank = previousRank == 0 ? null : previousRank - rank,
                    TrackId = candidate.TrackId,
                    CreatorId = candidate.CreatorId,
                    Title = candidate.Title,
                    Artist = candidate.Artist,
                    CoverArtUrl = candidate.CoverArtUrl,
                    Score = candidate.WeeklyQualifiedPlays,
                    PlaysInWindow = ToLegacyCount(candidate.WeeklyQualifiedPlays),
                    WeeklyQualifiedPlays = candidate.WeeklyQualifiedPlays,
                    LifetimePlays = candidate.LifetimePlays,
                    Basis = BasisWeeklyPlays,
                    DataThroughUtc = dataThrough,
                    ComputedAtUtc = generatedAt,
                });
                rank++;
            }

            if (rows.Count == 0)
            {
                // An internal rank-zero marker persists the current week's
                // freshness metadata even when no tracks are eligible. It is never
                // included in public entries or previous-week movement calculations.
                rows.Add(new WeeklyChartSnapshot
                {
                    Id = Guid.NewGuid(),
                    WeekStartUtc = weekStart,
                    WeekEndUtc = weekEnd,
                    Rank = 0,
                    TrackId = Guid.Empty,
                    CreatorId = string.Empty,
                    Title = string.Empty,
                    Artist = string.Empty,
                    Basis = BasisWeeklyPlays,
                    DataThroughUtc = dataThrough,
                    ComputedAtUtc = generatedAt,
                });
            }

            await repo.ReplaceWeekAsync(weekStart, rows, ct);

            // A different application instance may have committed a newer snapshot
            // while this one was calculating. Return the transaction winner.
            var persistedRows = await repo.GetWeekAsync(weekStart, ct);
            var response = ToResponse(
                persistedRows.Count == 0 ? rows : persistedRows,
                weekStart,
                generatedAt,
                forceStale: false);
            CambrianMetrics.WeeklyChartRecomputed.Add(1);
            return response;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            CambrianMetrics.WeeklyChartRecomputeFailed.Add(1);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private WeeklyChartsResponse ToResponse(
        IReadOnlyList<WeeklyChartSnapshot> rows,
        DateTime expectedWeekStart,
        DateTime observedAt,
        bool forceStale)
    {
        var metadata = rows
            .OrderBy(row => row.Rank)
            .FirstOrDefault();
        var chartWindowStart = metadata?.WeekStartUtc ?? expectedWeekStart;
        var chartWindowEnd = metadata?.WeekEndUtc ?? expectedWeekStart.AddDays(7);
        var generatedAt = rows.Count == 0
            ? observedAt
            : rows.Max(row => row.ComputedAtUtc);
        var dataThrough = rows.Count == 0
            ? null
            : rows.Max(row => row.DataThroughUtc);

        var rankedRows = rows
            .Where(row => row.Rank > 0)
            .OrderBy(row => row.Rank)
            .ToList();

        var entries = rankedRows
            .Select(row => new ChartEntryResponse
            {
                Rank = row.Rank,
                WeeklyQualifiedPlays = row.WeeklyQualifiedPlays,
                LifetimePlays = row.LifetimePlays,
                RankingScore = row.WeeklyQualifiedPlays,
                TrackId = row.TrackId.ToString(),
                Title = row.Title,
                Artist = row.Artist,
                CreatorId = row.CreatorId,
                CoverArtUrl = row.CoverArtUrl,
                DeltaRank = row.DeltaRank,
            })
            .ToList();

        var top = rankedRows.FirstOrDefault();
        var basis = metadata?.Basis ?? BasisWeeklyPlays;
        var isStale = forceStale
            || dataThrough is null
            || generatedAt < observedAt.Subtract(_staleAfter);

        return new WeeklyChartsResponse
        {
            // Legacy aliases retained additively for existing clients.
            WeekOf = chartWindowStart.ToString("o"),
            ComputedAt = generatedAt.ToString("o"),
            Entries = entries,
            Basis = basis,
            ChartWindowStart = chartWindowStart,
            ChartWindowEnd = chartWindowEnd,
            GeneratedAt = generatedAt,
            DataThrough = dataThrough,
            IsStale = isStale,
            TrackOfTheWeek = top is null ? null : new TrackOfTheWeekResponse
            {
                TrackId = top.TrackId.ToString(),
                Title = top.Title,
                Artist = top.Artist,
                CreatorId = top.CreatorId,
                CoverArtUrl = top.CoverArtUrl,
                Description = "This week's most-played track by qualified plays on The Scene.",
            },
        };
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static int ToLegacyCount(long count) =>
        count >= int.MaxValue ? int.MaxValue : (int)Math.Max(0, count);

    private static DateTime StartOfIsoWeekUtc(DateTime utc)
    {
        var date = DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
        var diff = (7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return date.AddDays(-diff);
    }
}
