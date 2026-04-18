using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cambrian.Api.Tests.Fixtures;

namespace Cambrian.Api.Tests.Security;

/// <summary>
/// Systematic authorization tests verifying that protected endpoints reject
/// unauthenticated and unauthorized requests, and that public endpoints
/// remain accessible without credentials.
/// Uses CambrianApiFixture for full integration testing through the middleware pipeline.
/// </summary>
public sealed class AuthorizationTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;
    private readonly HttpClient _anonymous;

    public AuthorizationTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
        _anonymous = fixture.CreateClient();
    }

    // ═══════════════════════════════════════════════════════════════
    // Public endpoints — should return 200 without auth
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("/health")]
    [InlineData("/discover")]
    [InlineData("/catalog")]
    [InlineData("/tracks")]
    [InlineData("/trending")]
    [InlineData("/subscriptions/plans")]
    public async Task PublicEndpoints_Return200_WithoutAuth(string path)
    {
        var response = await _anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════
    // Protected endpoints — should return 401 without auth
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("/library", "GET")]
    [InlineData("/subscriptions/current", "GET")]
    [InlineData("/subscriptions/history", "GET")]
    [InlineData("/billing/status", "GET")]
    [InlineData("/auth/me", "GET")]
    [InlineData("/stream", "GET")]
    public async Task ProtectedGetEndpoints_Return401_WithoutAuth(string path, string method)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await _anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/subscriptions/cancel")]
    [InlineData("/subscriptions/update")]
    public async Task ProtectedPostEndpoints_Return401_WithoutAuth(string path)
    {
        var response = await _anonymous.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════
    // Admin endpoints — should return 401 without auth
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("/admin/dashboard")]
    [InlineData("/admin/audit")]
    [InlineData("/admin/users")]
    [InlineData("/admin/tracks")]
    [InlineData("/admin/purchases")]
    [InlineData("/admin/payouts")]
    [InlineData("/admin/settings")]
    [InlineData("/admin/integrity")]
    [InlineData("/admin/storage-diagnostics")]
    public async Task AdminGetEndpoints_Return401_WithoutAuth(string path)
    {
        var response = await _anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/admin/users/fake-id/role")]
    [InlineData("/admin/users/fake-id/suspend")]
    [InlineData("/admin/users/fake-id/reactivate")]
    [InlineData("/admin/tracks/fake-id/remove")]
    [InlineData("/admin/tracks/fake-id/visibility")]
    public async Task AdminPostEndpoints_Return401_WithoutAuth(string path)
    {
        var response = await _anonymous.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════
    // Admin endpoints — should reject non-admin users (403)
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("/admin/dashboard", "get-dash")]
    [InlineData("/admin/users", "get-users")]
    [InlineData("/admin/tracks", "get-tracks")]
    [InlineData("/admin/payouts", "get-payouts")]
    [InlineData("/admin/settings", "get-settings")]
    public async Task AdminGetEndpoints_Return403_ForRegularUser(string path, string suffix)
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(
            $"reg-{suffix}@cambrian.com", "Test1234!@");

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/admin/users/fake-id/role", "post-role")]
    [InlineData("/admin/users/fake-id/suspend", "post-susp")]
    [InlineData("/admin/tracks/fake-id/remove", "post-rem")]
    public async Task AdminPostEndpoints_Return403_ForRegularUser(string path, string suffix)
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(
            $"reg-{suffix}@cambrian.com", "Test1234!@");

        var response = await client.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════
    // Authenticated user endpoints — should work with valid auth
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Library_ReturnsOk_ForAuthenticatedUser()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(
            "lib-authz@cambrian.com", "Test1234!@");

        var response = await client.GetAsync("/library");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuthMe_ReturnsOk_ForAuthenticatedUser()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(
            "me-authz@cambrian.com", "Test1234!@");

        var response = await client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SubscriptionsCurrent_ReturnsOk_ForAuthenticatedUser()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(
            "sub-authz@cambrian.com", "Test1234!@");

        var response = await client.GetAsync("/subscriptions/current");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════
    // Invalid token — should return 401
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task InvalidToken_Returns401()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "invalid.jwt.garbage");

        var response = await client.GetAsync("/library");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
