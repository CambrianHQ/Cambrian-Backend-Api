using Cambrian.Api.Tests.Fixtures;
using System.Net;

namespace Cambrian.Api.Tests;

/// <summary>
/// Verifies that the security-headers middleware emits
/// `Cross-Origin-Resource-Policy: cross-origin` on image and audio routes
/// (issue #71 — eliminates ORB issues for clients that bypass the Vercel proxy).
/// </summary>
public sealed class CorpHeaderTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _factory;

    public CorpHeaderTests(CambrianApiFixture factory) => _factory = factory;

    [Fact]
    public async Task ImageProxyResponse_HasCrossOriginResourcePolicyHeader()
    {
        var client = _factory.CreateClient();

        // The proxy will 404 for a missing key, but the security-headers
        // middleware sets CORP via OnStarting before the response is written,
        // so the header is present regardless of status code.
        var res = await client.GetAsync("/images/covers/does-not-exist.jpg");

        Assert.True(res.Headers.Contains("Cross-Origin-Resource-Policy"),
            "CORP header missing from /images/* response");
        Assert.Equal("cross-origin",
            string.Join("", res.Headers.GetValues("Cross-Origin-Resource-Policy")));
    }

    [Fact]
    public async Task ImageProxyResponse_HasImmutableCacheHeaders_StrongEtag_AndAllowedCorsOrigin()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/images/covers/test-cover.jpg");
        request.Headers.Add("Origin", "https://cambrianmusic.com");

        var res = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", res.Headers.CacheControl?.ToString());
        Assert.NotNull(res.Headers.ETag);
        Assert.False(res.Headers.ETag!.IsWeak);
        Assert.Equal("https://cambrianmusic.com", string.Join("", res.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task ImageProxyConditionalRequest_Returns304_WithCacheHeaders()
    {
        var client = _factory.CreateClient();
        var first = await client.GetAsync("/images/covers/test-cover.jpg");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(first.Headers.ETag);

        var conditional = new HttpRequestMessage(HttpMethod.Get, "/images/covers/test-cover.jpg");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.ToString());

        var second = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", second.Headers.CacheControl?.ToString());
        Assert.Equal(first.Headers.ETag, second.Headers.ETag);
    }

    [Fact]
    public async Task NonMediaResponse_DoesNotGetCorpHeader()
    {
        // Sanity: regular API responses (e.g. /health) should NOT carry CORP,
        // so we don't accidentally relax isolation for the whole API surface.
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/health");

        Assert.False(res.Headers.Contains("Cross-Origin-Resource-Policy"),
            "CORP header should only be set on image/audio paths, not /health");
    }
}
