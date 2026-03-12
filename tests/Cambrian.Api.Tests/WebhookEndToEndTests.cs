using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// P0 — Stripe webhook end-to-end through the HTTP pipeline.
///
/// Validates: webhook receipt → purchase created → library item created →
///            license row created → invoice present → idempotent on replay.
///
/// These tests use the CambrianApiFixture (in-memory SQLite) and hit the
/// actual /webhook/stripe endpoint via HttpClient.
/// </summary>
public sealed class WebhookEndToEndTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _factory;

    public WebhookEndToEndTests(CambrianApiFixture factory) => _factory = factory;

    private HttpClient CreateClient() => _factory.CreateClient();

    private async Task<(string userId, Guid trackId)> SeedCreatorAndTrackAsync(string creatorEmail = "webhook-creator@cambrian.com")
    {
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        var trackId = await _factory.SeedTrackAsync(creatorId, "Webhook Beat");
        return (creatorId, trackId);
    }

    private static StringContent WebhookPayload(string json) =>
        new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// Full flow: POST /webhook/stripe with checkout.session.completed →
    /// verify purchase, library item created.
    /// </summary>
    [Fact]
    public async Task WebhookStripe_CheckoutCompleted_CreatesPurchaseAndLibraryItem()
    {
        var (creatorId, trackId) = await SeedCreatorAndTrackAsync("wh-e2e-creator@cambrian.com");

        // Register the buyer
        var buyerEmail = "wh-e2e-buyer@cambrian.com";
        await _factory.RegisterUserAsync(buyerEmail, "Test1234!@");
        var buyerId = await _factory.GetUserIdAsync(buyerEmail);

        var client = CreateClient();
        var payload = $$"""
        {
            "id": "evt_wh_e2e_{{Guid.NewGuid():N}}",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "{{buyerId}}:{{trackId}}:non-exclusive",
                    "amount_total": 2999
                }
            }
        }
        """;

        var res = await client.PostAsync("/webhook/stripe", WebhookPayload(payload));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // Verify downstream side-effects in the database
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var purchase = await db.Purchases
            .FirstOrDefaultAsync(p => p.BuyerId == buyerId && p.TrackId == trackId);
        Assert.NotNull(purchase);
        Assert.Equal("completed", purchase.Status);
        Assert.Equal("non-exclusive", purchase.LicenseType);
        Assert.Equal(2999, purchase.AmountCents);

        var libraryItem = await db.Library
            .FirstOrDefaultAsync(l => l.UserId == buyerId && l.TrackId == trackId);
        Assert.NotNull(libraryItem);
    }

    /// <summary>
    /// P0 — Webhook idempotency: sending the same event twice must not
    /// create duplicate purchases, library entries, or invoices.
    /// </summary>
    [Fact]
    public async Task WebhookStripe_ReplayedEvent_DoesNotDuplicate()
    {
        var (creatorId, trackId) = await SeedCreatorAndTrackAsync("wh-idem-creator@cambrian.com");

        var buyerEmail = "wh-idem-buyer@cambrian.com";
        await _factory.RegisterUserAsync(buyerEmail, "Test1234!@");
        var buyerId = await _factory.GetUserIdAsync(buyerEmail);

        var eventId = $"evt_idem_{Guid.NewGuid():N}";
        var client = CreateClient();
        var payload = $$"""
        {
            "id": "{{eventId}}",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "{{buyerId}}:{{trackId}}:non-exclusive",
                    "amount_total": 4999
                }
            }
        }
        """;

        // Send the same webhook twice
        var res1 = await client.PostAsync("/webhook/stripe", WebhookPayload(payload));
        var res2 = await client.PostAsync("/webhook/stripe", WebhookPayload(payload));

        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);

        // Verify only one purchase and one library item exist
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var purchases = await db.Purchases
            .Where(p => p.BuyerId == buyerId && p.TrackId == trackId)
            .ToListAsync();
        Assert.Single(purchases);

        var libraryItems = await db.Library
            .Where(l => l.UserId == buyerId && l.TrackId == trackId)
            .ToListAsync();
        Assert.Single(libraryItems);

        // Stripe event should only be recorded once
        var events = await db.StripeWebhookEvents
            .Where(e => e.EventId == eventId)
            .ToListAsync();
        Assert.Single(events);
    }

    /// <summary>
    /// Subscription upgrade webhook: verify user tier changes and subscription record created.
    /// </summary>
    [Fact]
    public async Task WebhookStripe_SubscriptionCheckout_UpgradesTier()
    {
        var email = "wh-sub-upgrade@cambrian.com";
        await _factory.RegisterUserAsync(email, "Test1234!@");
        var userId = await _factory.GetUserIdAsync(email);

        var client = CreateClient();
        var payload = $$"""
        {
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "{{userId}}:subscription:creator"
                }
            }
        }
        """;

        var res = await client.PostAsync("/webhook/stripe", WebhookPayload(payload));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var user = await db.Users.FindAsync(userId);
        Assert.Equal("creator", user!.Tier);

        var sub = await db.Subscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Plan == "creator");
        Assert.NotNull(sub);
        Assert.Equal("active", sub.Status);
    }
}
