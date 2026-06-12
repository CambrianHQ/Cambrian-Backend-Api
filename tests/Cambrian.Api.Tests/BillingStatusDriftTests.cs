using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Enums;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// F8 — /billing/status must not disagree with the issued tier. A Pro account
/// without a Stripe subscription record (comped/seeded) must still report a Pro
/// plan, never "free", so the billing page and the tier claim can't drift.
/// </summary>
public class BillingStatusDriftTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public BillingStatusDriftTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Pro_without_subscription_reports_pro_not_free()
    {
        const string email = "billing-pro-nosub@test.com";
        var client = await _fixture.CreateRoleClientAsync(email, "Test1234!@", "Creator", "billingpronosub");
        await _fixture.SetCreatorTierAsync(email, CreatorTier.Pro);

        var res = await client.GetAsync("/billing/status");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.Equal("pro", data.GetProperty("tier").GetString());
        Assert.Equal("Pro", data.GetProperty("creatorTier").GetString());
    }

    [Fact]
    public async Task Free_without_subscription_reports_free()
    {
        const string email = "billing-free-nosub@test.com";
        var client = await _fixture.CreateRoleClientAsync(email, "Test1234!@", "Creator", "billingfreenosub");

        var res = await client.GetAsync("/billing/status");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.Equal("free", data.GetProperty("tier").GetString());
        Assert.Equal("Free", data.GetProperty("creatorTier").GetString());
    }
}
