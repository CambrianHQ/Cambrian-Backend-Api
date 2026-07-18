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

    /// <summary>
    /// Attempts to claim exclusive ownership of a (key, user, route) triple for a
    /// long-running, side-effecting operation (e.g. a track upload). Inserts a
    /// "processing" row backed by the DB-level unique index — this is what makes
    /// the claim safe across multiple concurrently running API instances, not an
    /// in-memory lock. Call this as the very first step of the operation, inside
    /// the same transaction that will create the underlying resource.
    ///
    /// A row stuck in "processing" for longer than <paramref name="staleAfter"/>
    /// (a crashed worker never reached <see cref="CompleteAsync"/> or
    /// <see cref="MarkFailedAsync"/>) is treated as abandoned and reclaimed, same
    /// as an explicitly "failed" row.
    /// </summary>
    Task<IdempotencyClaim> TryBeginAsync(string key, string userId, string routeKey, string requestHash, TimeSpan? staleAfter = null, CancellationToken ct = default);

    /// <summary>Marks a claimed key as completed with its final response, so future retries replay it verbatim.</summary>
    Task CompleteAsync(string key, string userId, string routeKey, int statusCode, string responseBody, TimeSpan? ttl = null, CancellationToken ct = default);

    /// <summary>Marks a claimed key as failed, so a legitimate retry with the same key can reclaim and re-attempt it.</summary>
    Task MarkFailedAsync(string key, string userId, string routeKey, CancellationToken ct = default);
}

public sealed record IdempotentResponse(int StatusCode, string Body);

/// <summary>Outcome of attempting to claim an idempotency key for a new operation.</summary>
public enum IdempotencyClaimOutcome
{
    /// <summary>No prior row (or a stale/failed one) existed — caller now owns the key and should proceed.</summary>
    Claimed,

    /// <summary>A prior call with the same key/payload already finished — replay its response, do not redo the work.</summary>
    Completed,

    /// <summary>A prior call with the same key/payload is still in flight — tell the client to wait/retry.</summary>
    Processing,

    /// <summary>The key was reused with a materially different payload — reject with 409 idempotency_key_reused.</summary>
    Mismatch,
}

public sealed record IdempotencyClaim(IdempotencyClaimOutcome Outcome, int? StatusCode = null, string? ResponseBody = null);
