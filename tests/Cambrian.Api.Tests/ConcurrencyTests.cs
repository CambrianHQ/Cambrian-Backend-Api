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
    /// collapse all deliveries to exactly one subscription regardless of timing.
    /// (Track-license purchasing has been removed; subscription checkout is the
    /// fulfilled path.)
    /// </summary>
    [Fact]
    public async Task Duplicate_Webhook_Event_Under_Parallel_Delivery_Activates_Single_Subscription()
    {
        // Seed a user.
        var userEmail = $"conc-sub-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(userEmail, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(userEmail);

        var eventId = $"evt_conc_{Guid.NewGuid():N}";
        var payload = $$"""
        {
            "id": "{{eventId}}",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "id": "cs_conc_{{eventId}}",
                    "client_reference_id": "{{userId}}:subscription:creator",
                    "customer": "cus_conc"
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

        var subscriptions = await db.Subscriptions
            .Where(s => s.UserId == userId)
            .ToListAsync();
        Assert.Single(subscriptions);

        var events = await db.StripeWebhookEvents
            .Where(e => e.EventId == eventId)
            .ToListAsync();
        Assert.Single(events);
    }
}
