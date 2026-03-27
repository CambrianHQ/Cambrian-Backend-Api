using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests.Payments;

/// <summary>
/// Contract tests for the Stripe webhook endpoint.
/// Validates processing, domain mapping, and idempotency.
/// </summary>
public sealed class WebhookContractTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _factory;

    public WebhookContractTests(CambrianApiFixture factory) => _factory = factory;

    private HttpClient CreateClient() => _factory.CreateClient();

    [Fact]
    public async Task Stripe_Webhook_Processes_Checkout_Completed()
    {
        // Seed creator + track
        var creatorEmail = "wh-contract-creator@cambrian.com";
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        var trackId = await _factory.SeedTrackAsync(creatorId, "Webhook Contract Beat");

        // Seed buyer
        var buyerEmail = "wh-contract-buyer@cambrian.com";
        await _factory.RegisterUserAsync(buyerEmail, "Test1234!@");
        var buyerId = await _factory.GetUserIdAsync(buyerEmail);

        var sessionId = $"cs_test_{Guid.NewGuid():N}";
        var eventId = $"evt_wh_contract_{Guid.NewGuid():N}";
        var payload = $$"""
        {
            "id": "{{eventId}}",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "id": "{{sessionId}}",
                    "client_reference_id": "{{buyerId}}:{{trackId}}:non-exclusive",
                    "amount_total": 2999,
                    "metadata": {
                        "trackId": "{{trackId}}",
                        "userId": "{{buyerId}}",
                        "licenseType": "non-exclusive"
                    }
                }
            }
        }
        """;

        var client = CreateClient();
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/webhook/stripe", content);

        response.EnsureSuccessStatusCode();

        // Verify domain side-effects: purchase and library item created
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var purchase = await db.Purchases
            .FirstOrDefaultAsync(p => p.TrackId == trackId && p.BuyerId == buyerId);
        Assert.NotNull(purchase);

        var libraryItem = await db.Library
            .FirstOrDefaultAsync(l => l.TrackId == trackId && l.UserId == buyerId);
        Assert.NotNull(libraryItem);
    }

    [Fact]
    public async Task Stripe_Webhook_Is_Idempotent_On_Replay()
    {
        // Seed creator + track
        var creatorEmail = "wh-idemp-creator@cambrian.com";
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        var trackId = await _factory.SeedTrackAsync(creatorId, "Idempotent Beat");

        // Seed buyer
        var buyerEmail = "wh-idemp-buyer@cambrian.com";
        await _factory.RegisterUserAsync(buyerEmail, "Test1234!@");
        var buyerId = await _factory.GetUserIdAsync(buyerEmail);

        var sessionId = $"cs_test_idemp_{Guid.NewGuid():N}";
        var eventId = $"evt_idemp_{Guid.NewGuid():N}";
        var payload = $$"""
        {
            "id": "{{eventId}}",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "id": "{{sessionId}}",
                    "client_reference_id": "{{buyerId}}:{{trackId}}:non-exclusive",
                    "amount_total": 2999,
                    "metadata": {
                        "trackId": "{{trackId}}",
                        "userId": "{{buyerId}}",
                        "licenseType": "non-exclusive"
                    }
                }
            }
        }
        """;

        var client = CreateClient();
        var content1 = new StringContent(payload, Encoding.UTF8, "application/json");
        var response1 = await client.PostAsync("/webhook/stripe", content1);
        response1.EnsureSuccessStatusCode();

        // Replay the same event
        var content2 = new StringContent(payload, Encoding.UTF8, "application/json");
        var response2 = await client.PostAsync("/webhook/stripe", content2);
        response2.EnsureSuccessStatusCode();

        // Should NOT create duplicate purchases
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var purchaseCount = await db.Purchases
            .CountAsync(p => p.TrackId == trackId && p.BuyerId == buyerId);

        Assert.Equal(1, purchaseCount);
    }

    [Fact]
    public async Task Stripe_Webhook_Returns_Ok_For_Unknown_Event_Types()
    {
        var payload = """
        {
            "id": "evt_unknown_type",
            "type": "customer.unknown_event",
            "data": {
                "object": {
                    "id": "cus_test_123"
                }
            }
        }
        """;

        var client = CreateClient();
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/webhook/stripe", content);

        // Unrecognized events should still return 200 (ack receipt to Stripe)
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Stripe_Webhook_Handles_Missing_Metadata_Gracefully()
    {
        var payload = $$"""
        {
            "id": "evt_no_meta_{{Guid.NewGuid():N}}",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "id": "cs_test_no_meta",
                    "client_reference_id": null,
                    "amount_total": 0
                }
            }
        }
        """;

        var client = CreateClient();
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/webhook/stripe", content);

        // Should handle gracefully — either succeed or return controlled error,
        // never 500 crash
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest,
            $"Expected 200 or 400 for missing metadata, got {response.StatusCode}");
    }
}
