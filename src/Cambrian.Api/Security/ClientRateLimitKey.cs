using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Cambrian.Api.Security;

public static class ClientRateLimitKey
{
    // Same header/cookie pair StreamController uses for anonymous playback sessions.
    private const string AnonymousSessionHeader = "X-Cambrian-Anonymous-Session";
    private const string AnonymousSessionCookie = "cambrian_playback_session";

    /// <summary>
    /// Uses only the connection address established by the server or a configured
    /// trusted-forwarder middleware. Raw X-Forwarded-For headers are never read.
    /// </summary>
    public static string FromConnection(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Partitions by authenticated user id when present, falling back to the
    /// connection address for anonymous requests. Behind a reverse proxy that
    /// isn't configured as a trusted forwarder, every client's connection address
    /// resolves to the same proxy hop — so an IP-only key can put every signed-in
    /// user's traffic in one shared bucket. Keying authenticated requests by user
    /// id keeps one user's request volume from exhausting another user's quota.
    /// </summary>
    public static string FromUserOrConnection(HttpContext context) =>
        context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? FromConnection(context);

    /// <summary>
    /// Playback partitioning, in priority order: authenticated user id → anonymous
    /// playback session (the X-Cambrian-Anonymous-Session header or the
    /// cambrian_playback_session cookie, hashed so the raw opaque value never
    /// becomes a partition key) → connection address. Behind Render's untrusted
    /// proxy every anonymous client shares one connection address, so keying by the
    /// playback session keeps one listener's ranged audio requests from exhausting
    /// every other anonymous listener's quota. The session value is client-chosen,
    /// so this only bounds accidental collapse (honest clients get isolated
    /// buckets) — it is not an anti-abuse identity. Deliberate floods that rotate
    /// session values are a DDoS concern handled at the Cloudflare layer, not here.
    /// </summary>
    public static string FromUserOrPlaybackSessionOrConnection(HttpContext context)
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(userId))
            return "user:" + userId;

        var session = context.Request.Headers[AnonymousSessionHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(session)
            && context.Request.Cookies.TryGetValue(AnonymousSessionCookie, out var cookie))
        {
            session = cookie;
        }

        if (!string.IsNullOrWhiteSpace(session))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(session));
            return "session:" + Convert.ToHexString(hash);
        }

        return "connection:" + FromConnection(context);
    }
}
