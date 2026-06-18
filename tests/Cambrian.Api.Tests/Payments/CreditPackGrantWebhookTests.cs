using System.Text;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests.Payments;

/// <summary>
/// Gate coverage for the purchased-credit webhook grant path
/// (StripeWebhookService.GrantPurchasedCredits) — previously untested. A completed
/// credit-pack checkout grants exactly N never-expiring purchased Release Ready
/// credits, the amount comes from the signed Stripe amount_total (never the client),
/// and duplicate deliveries are idempotent on the Stripe session id.
/// </summary>
public sealed class CreditPackGrantWebhookTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _factory;

    public CreditPackGrantWebhookTests(CambrianApiFixture factory) => _factory = factory;

    private static string CheckoutPayload(string eventId, string sessionId, string clientRef, long amountTotal) => $$"""
    {
        "id": "{{eventId}}",
        "type": "checkout.session.completed",
        "data": {
            "object": {
                "id": "{{sessionId}}",
                "client_reference_id": "{{clientRef}}",
                "amount_total": {{amountTotal}},
                "customer": "cus_credits"
            }
        }
    }
    """;

    private async Task<(string userId, HttpClient client)> NewUserAsync()
    {
        var email = $"credits-{Guid.NewGuid():N}@cambrian.com";
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
    public async Task CreditCheckout_Completed_Grants_Purchased_Credits_AtSignedAmount()
    {
        var (userId, client) = await NewUserAsync();
        var sessionId = $"cs_credits_{Guid.NewGuid():N}";

        // "triple" pack = 3 credits / 2400 cents (server-resolved into the clientReferenceId/amount).
        await PostAsync(client, CheckoutPayload($"evt_{Guid.NewGuid():N}", sessionId, $"{userId}:credits:3", 2400));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var rows = await db.ReleaseCreditPurchases.Where(p => p.CreatorId == userId).ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal(3, row.Credits);
        Assert.Equal(2400, row.AmountCents); // trusts the signed amount_total, not the client
        Assert.Equal("paid", row.Status);
        Assert.Equal(sessionId, row.StripeSessionId);
    }

    [Fact]
    public async Task CreditCheckout_DuplicateDelivery_SameSession_GrantsOnce()
    {
        var (userId, client) = await NewUserAsync();
        var sessionId = $"cs_credits_{Guid.NewGuid():N}";

        // Two DISTINCT events carrying the SAME Stripe session id (Stripe retry / double
        // delivery). Distinct event ids defeat the outer EventId dedup, so the inner
        // session-id guard is what must prevent the double grant.
        await PostAsync(client, CheckoutPayload($"evt_{Guid.NewGuid():N}", sessionId, $"{userId}:credits:10", 6900));
        await PostAsync(client, CheckoutPayload($"evt_{Guid.NewGuid():N}", sessionId, $"{userId}:credits:10", 6900));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var rows = await db.ReleaseCreditPurchases.Where(p => p.CreatorId == userId).ToListAsync();

        Assert.Single(rows);
        Assert.Equal(10, rows[0].Credits);
    }

    [Fact]
    public async Task CreditCheckout_DistinctSessions_GrantEachTime()
    {
        var (userId, client) = await NewUserAsync();

        await PostAsync(client, CheckoutPayload($"evt_{Guid.NewGuid():N}", $"cs_credits_{Guid.NewGuid():N}", $"{userId}:credits:1", 900));
        await PostAsync(client, CheckoutPayload($"evt_{Guid.NewGuid():N}", $"cs_credits_{Guid.NewGuid():N}", $"{userId}:credits:3", 2400));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var rows = await db.ReleaseCreditPurchases.Where(p => p.CreatorId == userId).ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(4, rows.Sum(r => r.Credits));
    }

    [Fact]
    public async Task CreditCheckout_ZeroCredits_GrantsNothing()
    {
        var (userId, client) = await NewUserAsync();

        // Guard: grantCredits must be > 0. A malformed ":credits:0" must not create a grant.
        await PostAsync(client, CheckoutPayload($"evt_{Guid.NewGuid():N}", $"cs_credits_{Guid.NewGuid():N}", $"{userId}:credits:0", 0));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var count = await db.ReleaseCreditPurchases.CountAsync(p => p.CreatorId == userId);

        Assert.Equal(0, count);
    }
}
