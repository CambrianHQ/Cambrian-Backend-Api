using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests.Regression;

[Trait("Category", "Critical")]
public sealed class PurchaseJourneyRegressionTests : IClassFixture<RelationalCambrianApiFixture>
{
    private readonly RelationalCambrianApiFixture _fixture;

    public PurchaseJourneyRegressionTests(RelationalCambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Upload_Checkout_Webhook_Download_GrantsSingleEntitlement()
    {
        var creator = await _fixture.CreateRoleClientAsync(
            "journey-creator@cambrian.com",
            role: "Creator",
            username: "journeycreator");

        using var uploadContent = new MultipartFormDataContent();
        uploadContent.Add(new StringContent("Regression Journey Beat"), "Title");
        uploadContent.Add(new StringContent("Hip-Hop"), "Genre");
        uploadContent.Add(new StringContent("9.99"), "Price");

        var audioBytes = new byte[] { 0xFF, 0xFB, 0x90, 0x00, 0x44, 0x41, 0x54, 0x41 };
        using var audioContent = new ByteArrayContent(audioBytes);
        audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
        uploadContent.Add(audioContent, "Audio", "journey-beat.mp3");

        var uploadResponse = await creator.PostAsync("/upload", uploadContent);
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var uploadJson = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var trackId = uploadJson.GetProperty("data").GetProperty("trackId").GetString();
        trackId.Should().NotBeNullOrWhiteSpace();

        var buyer = await _fixture.CreateAuthenticatedClientAsync("journey-buyer@cambrian.com");
        var checkoutResponse = await buyer.PostAsJsonAsync("/checkout", new
        {
            trackId,
            licenseType = "non-exclusive",
            usageType = "personal"
        });
        checkoutResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var checkoutJson = await checkoutResponse.Content.ReadFromJsonAsync<JsonElement>();
        var checkoutUrl = checkoutJson.GetProperty("data").GetProperty("checkoutUrl").GetString();
        var sessionId = checkoutUrl!.Split('/').Last();
        var buyerId = await _fixture.GetUserIdAsync("journey-buyer@cambrian.com");

        var eventId = $"evt_journey_{Guid.NewGuid():N}";
        var webhookPayload = $$"""
        {
          "id": "{{eventId}}",
          "type": "checkout.session.completed",
          "data": {
            "object": {
              "id": "{{sessionId}}",
              "customer": "cus_journey_local",
              "client_reference_id": "{{buyerId}}:{{trackId}}:non-exclusive:personal",
              "amount_total": 999
            }
          }
        }
        """;

        var webhook1 = await buyer.PostAsync("/webhook/stripe", new StringContent(webhookPayload, Encoding.UTF8, "application/json"));
        var webhook2 = await buyer.PostAsync("/webhook/stripe", new StringContent(webhookPayload, Encoding.UTF8, "application/json"));

        webhook1.StatusCode.Should().Be(HttpStatusCode.OK);
        webhook2.StatusCode.Should().Be(HttpStatusCode.OK);

        var downloadResponse = await buyer.GetAsync($"/download/{trackId}");
        downloadResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var downloadJson = await downloadResponse.Content.ReadFromJsonAsync<JsonElement>();
        downloadJson.GetProperty("data").GetProperty("url").GetString()
            .Should().NotBeNullOrWhiteSpace();

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var trackGuid = Guid.Parse(trackId!);

        db.Purchases.Should().ContainSingle(p =>
            p.BuyerId == buyerId &&
            p.TrackId == trackGuid &&
            p.Status == "completed");

        db.Library.Should().ContainSingle(l =>
            l.UserId == buyerId &&
            l.TrackId == trackGuid);

        db.StripeWebhookEvents.Should().ContainSingle(e => e.EventId == eventId);
    }
}
