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
            s.Plan == "creator" &&
            s.Status == "active");
    }
}
