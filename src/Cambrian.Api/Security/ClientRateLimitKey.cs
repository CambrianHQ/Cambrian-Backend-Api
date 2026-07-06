using System.Security.Claims;

namespace Cambrian.Api.Security;

public static class ClientRateLimitKey
{
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
}
