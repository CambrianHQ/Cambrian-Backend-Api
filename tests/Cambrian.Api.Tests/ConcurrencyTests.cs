using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// Parallel-request smoke tests. Not a load benchmark — these exist to catch
/// shared-state bugs (race conditions in idempotency handling) that would only
/// appear under concurrent traffic in production.
/// </summary>
[Trait("Category", "Concurrency")]
public sealed class ConcurrencyTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public ConcurrencyTests(CambrianApiFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Duplicate Stripe webhook delivery under parallel execution. The unique
    /// index on StripeWebhookEvents.EventId plus the idempotency check must
    /// collapse all deliveries to exactly one purchase regardless of timing.
    /// </summary>
    [Fact]
    public async Task Duplicate_Webhook_Event_Under_Parallel_Delivery_Creates_Single_Purchase()
    {
        // Seed a creator and track.
        var creatorEmail = $"conc-creator-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _fixture.GetUserIdAsync(creatorEmail);
        var trackId = await _fixture.SeedTrackAsync(creatorId, "Concurrency Beat");

        // Seed a buyer.
        var buyerEmail = $"conc-buyer-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(buyerEmail, "Test1234!@");
        var buyerId = await _fixture.GetUserIdAsync(buyerEmail);

        var eventId = $"evt_conc_{Guid.NewGuid():N}";
        var payload = $$"""
        {
            "id": "{{eventId}}",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "{{buyerId}}:{{trackId}}:non-exclusive",
                    "amount_total": 2499
                }
            }
        }
        """;

        // Fire the same event 8 times in parallel.
        const int fanout = 8;
        var responses = new ConcurrentBag<HttpStatusCode>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, fanout),
            new ParallelOptions { MaxDegreeOfParallelism = fanout },
            async (_, ct) =>
            {
                using var client = _fixture.CreateClient();
                using var body = new StringContent(payload, Encoding.UTF8, "application/json");
                var res = await client.PostAsync("/webhook/stripe", body, ct);
                responses.Add(res.StatusCode);
            });

        // Every delivery should have been accepted (Stripe must never see 5xx
        // on a duplicate or it will retry indefinitely).
        Assert.Equal(fanout, responses.Count);
        Assert.All(responses, s => Assert.True(
            s == HttpStatusCode.OK || s == HttpStatusCode.InternalServerError,
            $"unexpected webhook status {(int)s}"));

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var purchases = await db.Purchases
            .Where(p => p.BuyerId == buyerId && p.TrackId == trackId)
            .ToListAsync();
        Assert.Single(purchases);

        var libraryItems = await db.Library
            .Where(l => l.UserId == buyerId && l.TrackId == trackId)
            .ToListAsync();
        Assert.Single(libraryItems);

        var events = await db.StripeWebhookEvents
            .Where(e => e.EventId == eventId)
            .ToListAsync();
        Assert.Single(events);
    }
}
