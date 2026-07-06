using System.Net;
using System.Security.Claims;
using Cambrian.Api.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Cambrian.Api.Tests.Security;

/// <summary>
/// Regression: the global rate limiter and the "auth" policy partitioned solely by connection
/// address. Behind a reverse proxy that isn't a configured trusted forwarder (as in production —
/// see appsettings.Production.json RateLimiting/ForwardedHeaders config), every request's
/// connection address resolves to the same proxy hop, putting every signed-in user's traffic in
/// one shared bucket — a burst from any user (or any other endpoint) could exhaust the bucket
/// email-verification resend depends on for a completely different user. Partitioning
/// authenticated requests by user id instead removes the shared-bucket dependency on the network
/// topology entirely.
/// </summary>
public sealed class ClientRateLimitKeyTests
{
    [Fact]
    public void FromUserOrConnection_UsesUserId_WhenAuthenticated()
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "user-123") }, "test"));

        Assert.Equal("user-123", ClientRateLimitKey.FromUserOrConnection(ctx));
    }

    [Fact]
    public void FromUserOrConnection_FallsBackToConnection_WhenAnonymous()
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");

        Assert.Equal("10.0.0.5", ClientRateLimitKey.FromUserOrConnection(ctx));
    }

    [Fact]
    public void FromUserOrConnection_DifferentUsers_SameConnection_YieldDifferentKeys()
    {
        // The exact scenario behind the incident: two different signed-in users arriving through
        // the same proxy hop (same apparent connection address) must not share a partition.
        var ctxA = new DefaultHttpContext();
        ctxA.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
        ctxA.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "user-a") }, "test"));

        var ctxB = new DefaultHttpContext();
        ctxB.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
        ctxB.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "user-b") }, "test"));

        Assert.NotEqual(
            ClientRateLimitKey.FromUserOrConnection(ctxA),
            ClientRateLimitKey.FromUserOrConnection(ctxB));
    }
}
