namespace Cambrian.Domain.Entities;

/// <summary>
/// Stores the response body for a (key, user, route) triple so that retries of
/// non-idempotent public-API calls (e.g. POST /api/v1/licenses/purchase) return
/// the same result without re-executing the side effect (Stripe session
/// creation, etc.).
///
/// Scoped per-user so a leaked client-side Idempotency-Key from User A cannot
/// be used to read User B's response. Expires after a TTL (default 24h) — old
/// rows are swept by a background task; readers also self-evict on expired hits.
/// </summary>
public class ApiIdempotencyKey
{
    public Guid Id { get; set; }

    /// <summary>The Idempotency-Key header value supplied by the client.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Owning user id (ApplicationUser.Id). Scoped per-user — see class summary.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Logical route identifier, e.g. "POST /api/v1/licenses/purchase".</summary>
    public string RouteKey { get; set; } = string.Empty;

    /// <summary>JSON body returned to the client on the first successful call.</summary>
    public string ResponseBody { get; set; } = string.Empty;

    /// <summary>HTTP status returned on the first successful call.</summary>
    public int StatusCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// SHA-256 hex digest of the stable, non-file request fields (e.g. title +
    /// user id + file name/size for an upload). Lets a replay of the same key
    /// with a materially different payload be rejected as
    /// <c>idempotency_key_reused</c> instead of silently returning the wrong
    /// cached result. Null for older rows written before this field existed
    /// (e.g. any future non-upload idempotent callers that don't opt in).
    /// </summary>
    public string? RequestHash { get; set; }

    /// <summary>
    /// Claim lifecycle: "processing" (row inserted, work not finished yet),
    /// "completed" (ResponseBody/StatusCode are the final result), or "failed"
    /// (the underlying operation threw — safe for a legitimate retry to
    /// reclaim this key rather than being stuck forever). Defaults to
    /// "completed" for back-compat with the original cache-a-finished-response
    /// use case, where a row is only ever written once the call has already
    /// succeeded.
    /// </summary>
    public string Status { get; set; } = "completed";
}
