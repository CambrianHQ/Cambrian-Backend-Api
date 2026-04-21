namespace Cambrian.Application.Interfaces.V1;

/// <summary>
/// Server-side store for client Idempotency-Key replay protection on
/// non-idempotent public-API endpoints. Scoped per-user.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Returns the cached response (status + body) for a previously-served
    /// (key, user, route) triple. Returns null when no cache hit OR when the
    /// row is expired (also self-evicts expired rows).
    /// </summary>
    Task<IdempotentResponse?> TryGetAsync(string key, string userId, string routeKey, CancellationToken ct = default);

    /// <summary>
    /// Persists the response so subsequent retries with the same triple return
    /// the same body without re-executing. TTL defaults to 24 hours.
    /// </summary>
    Task SaveAsync(string key, string userId, string routeKey, int statusCode, string responseBody, TimeSpan? ttl = null, CancellationToken ct = default);
}

public sealed record IdempotentResponse(int StatusCode, string Body);
