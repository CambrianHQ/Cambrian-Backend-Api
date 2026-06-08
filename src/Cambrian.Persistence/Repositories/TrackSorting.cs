using Cambrian.Domain.Entities;

namespace Cambrian.Persistence.Repositories;

/// <summary>
/// Maps catalog sort tokens (the frontend-facing values plus legacy aliases) to a deterministic
/// server-side ordering. Extracted from <see cref="TrackRepository"/> so the mapping can be
/// unit-tested independently of EF/SQL (SQLite cannot ORDER BY decimal; PostgreSQL can).
/// </summary>
public static class TrackSorting
{
    public static IQueryable<Track> Apply(IQueryable<Track> query, string? sort)
        => sort?.Trim().ToLowerInvariant() switch
        {
            // Frontend-facing sort values (plus legacy aliases) → explicit server-side ordering.
            "pricelowtohigh" or "price_asc" or "price" => query.OrderBy(t => t.Price),
            "pricehightolow" or "price_desc" => query.OrderByDescending(t => t.Price),
            "newest" or "recent" or "created" => query.OrderByDescending(t => t.CreatedAt),
            "trending" or "popular" => query.OrderByDescending(t => t.TrendingScore)
                                            .ThenByDescending(t => t.CreatedAt),
            "title" => query.OrderBy(t => t.Title),
            _ => query.OrderByDescending(t => t.CreatedAt)
        };
}
