using System.Text;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests.Payments;

/// <summary>
/// Gate coverage for the subscription webhook grant path
/// (StripeWebhookService.HandleSubscriptionCheckout). A completed subscription
/// checkout creates exactly one active Subscription and upgrades the user's tier,
/// and — critically — duplicate deliveries of the SAME Stripe checkout session are
/// idempotent (regression: previously the handler had no session-id guard, so a
/// retried/duplicate checkout.session.completed created duplicate Subscription rows
/// and re-applied the tier). Idempotency is backed by a unique filtered index on
/// Subscriptions.StripeSessionId, mirroring credit-pack / purchase idempotency.
/// </summary>
public sealed class SubscriptionGrantWebhookTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _factory;

    public SubscriptionGrantWebhookTests(CambrianApiFixture factory) => _factory = factory;

    private static string CheckoutPayload(string eventId, string sessionId, string clientRef) => $$"""
    {
        "id": "{{eventId}}",
        "type": "checkout.session.completed",
        "data": {
            "object": {
                "id": "{{sessionId}}",
                "client_reference_id": "{{clientRef}}",
                "customer": "cus_sub_{{sessionId}}",
                "subscription": "sub_{{sessionId}}"
            }
        }
    }
    """;

    private async Task<(string userId, HttpClient client)> NewUserAsync()
    {
        var email = $"sub-{Guid.NewGuid():N}@cambrian.com";
        await _factory.RegisterUserAsync(email, "Test1234!@");
        var userId = await _factory.GetUserIdAsync(email);
        return (userId, _factory.CreateClient());
    }

    private async Task PostAsync(HttpClient client, string payload)
    {
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/webhook/stripe", content);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task SubscriptionCheckout_Completed_Creates_Active_Subscription_And_Upgrades_Tier()
    {
        var (userId, client) = await NewUserAsync();
        var sessionId = $"cs_sub_{Guid.NewGuid():N}";

        await PostAsync(client, CheckoutPayload($"evt_{Guid.NewGuid():N}", sessionId, $"{userId}:subscription:creator"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var subs = await db.Subscriptions.Where(s => s.UserId == userId).ToListAsync();
        var sub = Assert.Single(subs);
        Assert.Equal("active", sub.Status);
        Assert.Equal("creator", sub.Plan);
        Assert.Equal(sessionId, sub.StripeSessionId);

        var user = await db.Users.FindAsync(userId);
        Assert.NotNull(user);
        Assert.Equal("creator", user!.Tier);
    }

    [Fact]
    public async Task SubscriptionCheckout_DuplicateDelivery_SameSession_CreatesOnce()
    {
        var (userId, client) = await NewUserAsync();
        var sessionId = $"cs_sub_{Guid.NewGuid():N}";

        // Two DISTINCT events carrying the SAME Stripe session id (Stripe retry / double
        // delivery). Distinct event ids defeat the outer EventId dedup, so the inner
        // session-id guard is what must prevent a second Subscription row + tier re-apply.
        await PostAsync(client, CheckoutPayload($"evt_{Guid.NewGuid():N}", sessionId, $"{userId}:subscription:creator"));
        await PostAsync(client, CheckoutPayload($"evt_{Guid.NewGuid():N}", sessionId, $"{userId}:subscription:creator"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var subs = await db.Subscriptions.Where(s => s.UserId == userId).ToListAsync();
        Assert.Single(subs); // not two (one active + one cancelled) — the duplicate is a no-op
        Assert.Equal("active", subs[0].Status);
    }

    [Fact]
    public async Task SubscriptionCheckout_DistinctSessions_Upgrade_Then_Resubscribe()
    {
        var (userId, client) = await NewUserAsync();

        // Distinct sessions are genuinely distinct events (e.g. upgrade Creator -> Pro):
        // the first subscription is cancelled and a new active one is created.
        await PostAsync(client, CheckoutPayload($"evt_{Guid.NewGuid():N}", $"cs_sub_{Guid.NewGuid():N}", $"{userId}:subscription:creator"));
        await PostAsync(client, CheckoutPayload($"evt_{Guid.NewGuid():N}", $"cs_sub_{Guid.NewGuid():N}", $"{userId}:subscription:pro"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var subs = await db.Subscriptions.Where(s => s.UserId == userId).ToListAsync();
        Assert.Equal(2, subs.Count);
        Assert.Single(subs, s => s.Status == "active" && s.Plan == "pro");
        Assert.Single(subs, s => s.Status == "cancelled" && s.Plan == "creator");

        var user = await db.Users.FindAsync(userId);
        Assert.Equal("pro", user!.Tier);
    }
}
