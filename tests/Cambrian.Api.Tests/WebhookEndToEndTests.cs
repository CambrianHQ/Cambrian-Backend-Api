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
/// Validates: subscription checkout fulfillment, and that the removed track-license
/// purchase path no longer creates Purchase/Library/License rows.
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
    /// Track-license purchasing is removed: a legacy track-purchase webhook must be
    /// acknowledged (200) but create no Purchase, Library item, or license certificate.
    /// </summary>
    [Fact]
    public async Task WebhookStripe_TrackPurchaseReference_CreatesNothing()
    {
        var (creatorId, trackId) = await SeedCreatorAndTrackAsync("wh-e2e-creator@cambrian.com");

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

        // Acknowledged so Stripe does not retry, but nothing is fulfilled.
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        Assert.False(await db.Purchases.AnyAsync(p => p.BuyerId == buyerId && p.TrackId == trackId));
        Assert.False(await db.Library.AnyAsync(l => l.UserId == buyerId && l.TrackId == trackId));
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
            "id": "evt_wh_sub_{{Guid.NewGuid():N}}",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "id": "cs_wh_sub_{{Guid.NewGuid():N}}",
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
