using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;

namespace Cambrian.Api.Tests;

public sealed class PublicEntitlementsConfigTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public PublicEntitlementsConfigTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetEntitlementsConfig_ReturnsTierTruth_WithFiveMinuteCache()
    {
        var client = _fixture.CreateClient();

        var response = await client.GetAsync("/api/config/entitlements");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=300", response.Headers.CacheControl?.ToString());

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var plans = json.GetProperty("plans").EnumerateArray().ToList();

        var free = plans.Single(p => p.GetProperty("slug").GetString() == "free");
        var creator = plans.Single(p => p.GetProperty("slug").GetString() == "creator");
        var pro = plans.Single(p => p.GetProperty("slug").GetString() == "pro");

        Assert.Equal(10, free.GetProperty("maxTracks").GetInt32());
        Assert.Equal(3, creator.GetProperty("releaseReadyCreditsPerMonth").GetInt32());
        Assert.Equal(10, pro.GetProperty("releaseReadyCreditsPerMonth").GetInt32());

        var packs = json.GetProperty("releaseReady").GetProperty("creditPacks").EnumerateArray().ToList();
        Assert.Equal(new[] { 1, 3, 10 }, packs.Select(p => p.GetProperty("credits").GetInt32()).ToArray());
        Assert.False(json.GetProperty("releaseReady").GetProperty("creditExpiry").GetProperty("purchasedCreditsExpire").GetBoolean());
    }
}
