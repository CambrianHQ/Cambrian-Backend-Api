using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests.Integration.Api;

public sealed class CheckoutAndBillingApiTests : IClassFixture<RelationalCambrianApiFixture>
{
    private readonly RelationalCambrianApiFixture _fixture;

    public CheckoutAndBillingApiTests(RelationalCambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PostCheckout_ThenConfirmSession_CreatesCompletedPurchase_AndLibraryRecord()
    {
        await _fixture.RegisterUserAsync("checkout-api-creator@cambrian.com");
        var creatorId = await _fixture.GetUserIdAsync("checkout-api-creator@cambrian.com");
        var trackId = await _fixture.SeedTrackAsync(creatorId, "API Checkout Beat");

        var buyer = await _fixture.CreateAuthenticatedClientAsync("checkout-api-buyer@cambrian.com");

        var checkoutResponse = await buyer.PostAsJsonAsync("/checkout", new
        {
            trackId = trackId.ToString(),
            licenseType = "non-exclusive",
            usageType = "personal"
        });

        checkoutResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var checkoutJson = await checkoutResponse.Content.ReadFromJsonAsync<JsonElement>();
        var checkoutUrl = checkoutJson.GetProperty("data").GetProperty("checkoutUrl").GetString();
        checkoutUrl.Should().NotBeNullOrWhiteSpace();

        var sessionId = checkoutUrl!.Split('/').Last();
        var confirmResponse = await buyer.GetAsync($"/checkout/session/{sessionId}");
        confirmResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var buyerId = await _fixture.GetUserIdAsync("checkout-api-buyer@cambrian.com");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        db.Purchases.Should().ContainSingle(p =>
            p.BuyerId == buyerId &&
            p.TrackId == trackId &&
            p.Status == "completed" &&
            p.StripeSessionId == sessionId);

        db.Library.Should().ContainSingle(l =>
            l.UserId == buyerId &&
            l.TrackId == trackId);
    }

    [Fact]
    public async Task BillingCheckout_ThenConfirmSession_CreatesActiveSubscription()
    {
        var buyer = await _fixture.CreateAuthenticatedClientAsync("billing-api-user@cambrian.com");

        var checkoutResponse = await buyer.PostAsJsonAsync("/billing/checkout", new
        {
            tier = "creator"
        });

        checkoutResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var checkoutJson = await checkoutResponse.Content.ReadFromJsonAsync<JsonElement>();
        var checkoutUrl = checkoutJson.GetProperty("data").GetProperty("checkoutUrl").GetString();
        var sessionId = checkoutUrl!.Split('/').Last();

        var confirmResponse = await buyer.GetAsync($"/billing/checkout-session/{sessionId}");
        confirmResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var buyerId = await _fixture.GetUserIdAsync("billing-api-user@cambrian.com");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        db.Subscriptions.Should().ContainSingle(s =>
            s.UserId == buyerId &&
            s.Plan == "pro" &&
            s.Status == "active");
    }
}
