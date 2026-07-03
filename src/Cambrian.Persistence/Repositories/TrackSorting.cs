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
        // Every branch ends with a `.ThenBy(t => t.Id)` tie-break so the ordering is
        // TOTAL and deterministic. Without it, rows sharing a sort key (e.g. the same
        // CreatedAt for a batch import, or an equal price) could come back in a different
        // order per query, causing paginated results to shuffle across pages.
        => sort?.Trim().ToLowerInvariant() switch
        {
            // Frontend-facing sort values (plus legacy aliases) → explicit server-side ordering.
            "pricelowtohigh" or "price_asc" or "price" => query.OrderBy(t => t.Price).ThenBy(t => t.Id),
            "pricehightolow" or "price_desc" => query.OrderByDescending(t => t.Price).ThenBy(t => t.Id),
            "newest" or "recent" or "created" => query.OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id),
            // Cast the decimal score to double so the ORDER BY is provider-portable: SQLite
            // throws on ORDER BY a decimal expression but can order CAST(... AS REAL); PostgreSQL
            // handles both. Ordering is otherwise identical (monotonic cast).
            "trending" or "popular" => query.OrderByDescending(t => (double)t.TrendingScore)
                                            .ThenByDescending(t => t.CreatedAt)
                                            .ThenBy(t => t.Id),
            "title" => query.OrderBy(t => t.Title).ThenBy(t => t.Id),
            _ => query.OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
        };
}
