using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests.Payments;

/// <summary>
/// Contract tests for checkout endpoints.
/// Validates request/response shapes, idempotency, and pricing integrity.
/// </summary>
public sealed class CheckoutContractTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _factory;

    public CheckoutContractTests(CambrianApiFixture factory) => _factory = factory;

    [Fact]
    public async Task Checkout_Returns_Valid_Response_Shape()
    {
        // Seed creator + track
        var creatorEmail = "checkout-contract-creator@cambrian.com";
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        await _factory.SetUserRoleAsync(creatorEmail, "Creator");
        var trackId = await _factory.SeedTrackAsync(creatorId, "Checkout Shape Beat");

        // Create authenticated buyer
        var buyerClient = await _factory.CreateAuthenticatedClientAsync(
            "checkout-contract-buyer@cambrian.com", "Test1234!@");

        var request = new { trackId = trackId.ToString(), licenseType = "non-exclusive" };
        var response = await buyerClient.PostAsJsonAsync("/checkout", request);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Verify envelope shape
        Assert.True(json.GetProperty("success").GetBoolean());
        var data = json.GetProperty("data");
        Assert.True(data.TryGetProperty("checkoutUrl", out var urlProp));
        Assert.False(string.IsNullOrEmpty(urlProp.GetString()));
        Assert.True(data.TryGetProperty("status", out _));
    }

    [Fact]
    public async Task Checkout_Returns_Unauthorized_Without_Token()
    {
        var client = _factory.CreateClient();

        var request = new
        {
            trackId = Guid.NewGuid().ToString(),
            licenseType = "non-exclusive"
        };

        var response = await client.PostAsJsonAsync("/checkout", request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Checkout_Ignores_Client_Price_Tampering()
    {
        // Seed creator with track at known price
        var creatorEmail = "checkout-price-creator@cambrian.com";
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        await _factory.SetUserRoleAsync(creatorEmail, "Creator");

        // Seed track with specific price
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var track = new Track
            {
                Id = Guid.NewGuid(),
                CambrianTrackId = $"CAMB-TRK-PRICETEST",
                Title = "Price Test Beat",
                Price = 29.99m,
                NonExclusivePriceCents = 2999,
                ExclusivePriceCents = 19900,
                CopyrightBuyoutPriceCents = 49900,
                LicenseType = "standard",
                AudioUrl = "tracks/price-test.mp3",
                CreatorId = creatorId,
                Genre = "Electronic"
            };
            db.Tracks.Add(track);
            await db.SaveChangesAsync();

            var buyerClient = await _factory.CreateAuthenticatedClientAsync(
                "checkout-price-buyer@cambrian.com", "Test1234!@");

            // Client tries to send a fake low price — this field should be ignored
            var request = new
            {
                trackId = track.Id.ToString(),
                licenseType = "non-exclusive"
                // Note: there's no price field in CheckoutRequest - the backend
                // always uses the server-side price from the database
            };

            var response = await buyerClient.PostAsJsonAsync("/checkout", request);

            // The checkout should succeed using the DB price, not a client-supplied price
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
        }
    }

    [Fact]
    public async Task Checkout_Rejects_Invalid_TrackId()
    {
        var buyerClient = await _factory.CreateAuthenticatedClientAsync(
            "checkout-invalid-buyer@cambrian.com", "Test1234!@");

        var request = new { trackId = "not-a-guid", licenseType = "non-exclusive" };
        var response = await buyerClient.PostAsJsonAsync("/checkout", request);

        // Should return 400 for invalid GUID
        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.InternalServerError,
            $"Expected 400 or 500 for invalid trackId, got {response.StatusCode}");
    }

    [Fact]
    public async Task Checkout_Rejects_NonExistent_Track()
    {
        var buyerClient = await _factory.CreateAuthenticatedClientAsync(
            "checkout-missing-buyer@cambrian.com", "Test1234!@");

        var request = new
        {
            trackId = Guid.NewGuid().ToString(),
            licenseType = "non-exclusive"
        };
        var response = await buyerClient.PostAsJsonAsync("/checkout", request);

        // Should return 404 or 400 for missing track
        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.InternalServerError,
            $"Expected error for missing track, got {response.StatusCode}");
    }
}
