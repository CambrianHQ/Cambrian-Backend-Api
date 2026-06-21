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
    public async Task Stripe_Webhook_Processes_Subscription_Checkout_Completed()
    {
        // Track-license purchasing is removed; subscription checkout is the fulfilled path.
        var email = "wh-contract-sub@cambrian.com";
        await _factory.RegisterUserAsync(email, "Test1234!@");
        var userId = await _factory.GetUserIdAsync(email);

        var eventId = $"evt_wh_contract_{Guid.NewGuid():N}";
        var payload = $$"""
        {
            "id": "{{eventId}}",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "id": "cs_test_{{Guid.NewGuid():N}}",
                    "client_reference_id": "{{userId}}:subscription:creator",
                    "customer": "cus_wh_contract"
                }
            }
        }
        """;

        var client = CreateClient();
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/webhook/stripe", content);

        response.EnsureSuccessStatusCode();

        // Verify domain side-effect: subscription activated.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId);
        Assert.NotNull(sub);
        Assert.Equal("active", sub.Status);
        Assert.Equal("creator", sub.Plan);
    }

    [Fact]
    public async Task Stripe_Webhook_Is_Idempotent_On_Replay()
    {
        var email = "wh-idemp-sub@cambrian.com";
        await _factory.RegisterUserAsync(email, "Test1234!@");
        var userId = await _factory.GetUserIdAsync(email);

        var eventId = $"evt_idemp_{Guid.NewGuid():N}";
        var payload = $$"""
        {
            "id": "{{eventId}}",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "id": "cs_test_idemp_{{Guid.NewGuid():N}}",
                    "client_reference_id": "{{userId}}:subscription:creator",
                    "customer": "cus_idemp"
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

        // Should NOT create duplicate subscriptions
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var subCount = await db.Subscriptions.CountAsync(s => s.UserId == userId);

        Assert.Equal(1, subCount);
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

        // A paid event without fulfillment metadata must remain retryable. Returning
        // 500 tells Stripe to retry while the failed event is retained in the ledger.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
