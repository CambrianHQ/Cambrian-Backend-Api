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
}
