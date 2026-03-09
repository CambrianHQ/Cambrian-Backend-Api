using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;

namespace Cambrian.Api.Tests;

/// <summary>
/// Integration tests for the Download flow:
///   GET /download/{trackId}       → signed URL (requires purchase)
///   GET /download/{trackId}/signed → signed URL with expiry
/// </summary>
public sealed class DownloadTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _factory;

    public DownloadTests(CambrianApiFixture factory) => _factory = factory;

    [Fact]
    public async Task Download_WithPurchase_ReturnsSignedUrl()
    {
        // Seed creator + track
        var creatorEmail = "dl-creator@cambrian.com";
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        var trackId = await _factory.SeedTrackAsync(creatorId, "Download Beat");

        // Register buyer + add to library (simulates completed purchase)
        var buyerEmail = "dl-buyer@cambrian.com";
        var client = await _factory.CreateAuthenticatedClientAsync(buyerEmail, "Test1234!@");
        var buyerId = await _factory.GetUserIdAsync(buyerEmail);
        await _factory.SeedLibraryItemAsync(buyerId, trackId);

        var res = await client.GetAsync($"/download/{trackId}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        var url = json.GetProperty("data").GetProperty("url").GetString();
        Assert.Contains("signed=true", url);
    }

    [Fact]
    public async Task Download_WithoutPurchase_Returns403()
    {
        // Seed creator + track
        var creatorEmail = "dl-nopurch-creator@cambrian.com";
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        var trackId = await _factory.SeedTrackAsync(creatorId, "Locked Beat");

        // Buyer has NOT purchased
        var client = await _factory.CreateAuthenticatedClientAsync(
            "dl-nopurch-buyer@cambrian.com", "Test1234!@");

        var res = await client.GetAsync($"/download/{trackId}");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Download_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync($"/download/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Download_InvalidGuid_Returns400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync(
            "dl-bad-id@cambrian.com", "Test1234!@");

        var res = await client.GetAsync("/download/not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SignedUrl_WithPurchase_ReturnsUrlAndExpiry()
    {
        // Seed creator + track
        var creatorEmail = "dl-signed-creator@cambrian.com";
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        var trackId = await _factory.SeedTrackAsync(creatorId, "Signed URL Beat");

        // Buyer with library item
        var buyerEmail = "dl-signed-buyer@cambrian.com";
        var client = await _factory.CreateAuthenticatedClientAsync(buyerEmail, "Test1234!@");
        var buyerId = await _factory.GetUserIdAsync(buyerEmail);
        await _factory.SeedLibraryItemAsync(buyerId, trackId);

        var res = await client.GetAsync($"/download/{trackId}/signed");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());

        var data = json.GetProperty("data");
        Assert.False(string.IsNullOrEmpty(data.GetProperty("signedUrl").GetString()));
        Assert.True(data.TryGetProperty("expiresAt", out _));
    }
}
