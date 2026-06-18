using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;

namespace Cambrian.Api.Tests.Integration.Api;

/// <summary>
/// Commissions/collabs are advertised as a paid feature but not implemented yet.
/// The backend exposes a clear not-implemented surface so the frontend can hide the
/// paid CTA pre-launch: an authenticated POST /commissions returns 501, and
/// /api/me/entitlements reports commissionsEnabled=false.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CommissionsEndpointTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public CommissionsEndpointTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Commissions_Anonymous_IsRejected()
    {
        using var client = _fixture.CreateClient();
        var resp = await client.PostAsync("/commissions", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Commissions_Authenticated_Returns501_NotImplemented()
    {
        var email = $"comm-{Guid.NewGuid():N}@cambrian.com";
        using var client = await _fixture.CreateAuthenticatedClientAsync(email, "Test1234!@");

        var resp = await client.PostAsync("/commissions", null);

        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("commissions_not_implemented", body);
    }

    [Fact]
    public async Task Entitlements_ReportCommissionsDisabled()
    {
        var email = $"comm-ent-{Guid.NewGuid():N}@cambrian.com";
        using var client = await _fixture.CreateAuthenticatedClientAsync(email, "Test1234!@");

        var resp = await client.GetAsync("/api/me/entitlements");
        Assert.True(resp.IsSuccessStatusCode, $"Expected 2xx, got {(int)resp.StatusCode}");

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var features = json.GetProperty("data").GetProperty("features");
        Assert.False(features.GetProperty("commissionsEnabled").GetBoolean());
    }
}
