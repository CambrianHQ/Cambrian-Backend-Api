using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;

namespace Cambrian.Api.Tests;

public sealed class PayoutFeatureGateTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public PayoutFeatureGateTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("GET", "/payouts/history")]
    [InlineData("GET", "/payouts/earnings")]
    [InlineData("GET", "/payouts/connect-status")]
    [InlineData("GET", "/payouts/account")]
    [InlineData("GET", "/earnings")]
    [InlineData("POST", "/payouts/connect")]
    [InlineData("POST", "/payouts/connect-stripe")]
    [InlineData("POST", "/payouts/disconnect")]
    [InlineData("DELETE", "/payouts/disconnect")]
    [InlineData("POST", "/payouts/request")]
    public async Task PayoutEndpoints_ReturnStructuredFailure_WhenStripeConnectDisabled(string method, string path)
    {
        await _fixture.SetFeatureFlagAsync("StripeConnectEnabled", false);
        try
        {
            using var client = await CreateCreatorClientAsync();

            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(path, "/payouts/request", StringComparison.Ordinal))
            {
                request.Content = JsonContent.Create(new { amount = 10m });
            }

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("STRIPE_NOT_READY", json.GetProperty("code").GetString());
            Assert.Equal("Payouts not enabled yet", json.GetProperty("message").GetString());
        }
        finally
        {
            await _fixture.SetFeatureFlagAsync("StripeConnectEnabled", true);
        }
    }

    [Fact]
    public async Task PayoutHistory_ReturnsNormalFlow_WhenStripeConnectEnabled()
    {
        await _fixture.SetFeatureFlagAsync("StripeConnectEnabled", true);
        using var client = await CreateCreatorClientAsync();

        var response = await client.GetAsync("/payouts/history");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Array, json.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task AdminStripeStatus_ReportsDisabledFlag()
    {
        await _fixture.SetFeatureFlagAsync("StripeConnectEnabled", false);
        try
        {
            using var client = await CreateAdminClientAsync();

            var response = await client.GetAsync("/admin/integrations/stripe/status");

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = json.GetProperty("data");
            Assert.False(data.GetProperty("stripeConnectEnabled").GetBoolean());
            Assert.False(data.GetProperty("ready").GetBoolean());
            Assert.Equal("disabled", data.GetProperty("status").GetString());
        }
        finally
        {
            await _fixture.SetFeatureFlagAsync("StripeConnectEnabled", true);
        }
    }

    private Task<HttpClient> CreateCreatorClientAsync()
    {
        var seed = Guid.NewGuid().ToString("N");
        return _fixture.CreateRoleClientAsync(
            email: $"payout-gate-creator-{seed}@cambrian.com",
            password: "Test1234!@",
            role: "Creator",
            username: $"pgcreator{seed[..8]}");
    }

    private Task<HttpClient> CreateAdminClientAsync()
    {
        var seed = Guid.NewGuid().ToString("N");
        return _fixture.CreateRoleClientAsync(
            email: $"payout-gate-admin-{seed}@cambrian.com",
            password: "Test1234!@",
            role: "Admin");
    }
}
