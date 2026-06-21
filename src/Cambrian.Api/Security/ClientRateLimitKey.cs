namespace Cambrian.Api.Security;

public static class ClientRateLimitKey
{
    /// <summary>
    /// Uses only the connection address established by the server or a configured
    /// trusted-forwarder middleware. Raw X-Forwarded-For headers are never read.
    /// </summary>
    public static string FromConnection(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
