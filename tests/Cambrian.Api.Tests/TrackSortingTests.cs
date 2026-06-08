using Cambrian.Domain.Entities;
using Cambrian.Persistence.Repositories;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// B11 regression: the catalog sort token must map to the correct server-side ordering. Previously
/// only price/price_desc/title were recognized, so the documented values priceLowToHigh,
/// priceHighToLow, newest and trending all fell through to a single default order.
///
/// Tested against LINQ-to-Objects so the assertion is exact and DB-agnostic (SQLite cannot
/// ORDER BY decimal; PostgreSQL — the production database — can).
/// </summary>
public sealed class TrackSortingTests
{
    private static IQueryable<Track> Sample() => new List<Track>
    {
        new() { Title = "Bravo",   Price = 10m, TrendingScore = 1m, CreatedAt = new DateTime(2026, 1, 2) },
        new() { Title = "Alpha",   Price = 5m,  TrendingScore = 3m, CreatedAt = new DateTime(2026, 1, 3) },
        new() { Title = "Charlie", Price = 15m, TrendingScore = 2m, CreatedAt = new DateTime(2026, 1, 1) },
    }.AsQueryable();

    [Fact]
    public void PriceLowToHigh_OrdersByPriceAscending()
        => Assert.Equal(new[] { 5m, 10m, 15m },
            TrackSorting.Apply(Sample(), "priceLowToHigh").Select(t => t.Price).ToArray());

    [Fact]
    public void PriceHighToLow_OrdersByPriceDescending()
        => Assert.Equal(new[] { 15m, 10m, 5m },
            TrackSorting.Apply(Sample(), "priceHighToLow").Select(t => t.Price).ToArray());

    [Fact]
    public void Newest_OrdersByCreatedAtDescending()
        => Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" },
            TrackSorting.Apply(Sample(), "newest").Select(t => t.Title).ToArray());

    [Fact]
    public void Trending_OrdersByTrendingScoreDescending()
        => Assert.Equal(new[] { "Alpha", "Charlie", "Bravo" },
            TrackSorting.Apply(Sample(), "trending").Select(t => t.Title).ToArray());

    [Fact]
    public void Unknown_FallsBackToNewest()
        => Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" },
            TrackSorting.Apply(Sample(), "something-else").Select(t => t.Title).ToArray());
}
