using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests.Payments;

[Trait("Category", "Postgres")]
public sealed class SignedPostgresPaymentFulfillmentTests : IClassFixture<SignedRelationalStripeWebhookApiFixture>
{
    private readonly SignedRelationalStripeWebhookApiFixture _fixture;
    public SignedPostgresPaymentFulfillmentTests(SignedRelationalStripeWebhookApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SignedSubscriptionCheckout_RequiresConfiguredPrice_AndPollingCannotGrant()
    {
        RequirePostgres();
        var email = $"price-proof-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var invalid = SubscriptionCheckoutEvent(userId, "price_attacker", $"evt_{Guid.NewGuid():N}", $"cs_{Guid.NewGuid():N}");
        (await PostPlatformAsync(invalid)).StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            (await db.Users.FindAsync(userId))!.CreatorTier.Should().Be(CreatorTier.Free);
            (await db.Subscriptions.AnyAsync(s => s.UserId == userId)).Should().BeFalse();
        }

        var valid = SubscriptionCheckoutEvent(userId, "price_test_creator", $"evt_{Guid.NewGuid():N}", $"cs_{Guid.NewGuid():N}");
        (await PostPlatformAsync(valid)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var verifyScope = _fixture.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        (await verifyDb.Users.FindAsync(userId))!.CreatorTier.Should().Be(CreatorTier.Creator);
        (await verifyDb.Subscriptions.SingleAsync(s => s.UserId == userId)).StripeSubscriptionId.Should().NotBeNull();
    }

    [Fact]
    public async Task ConcurrentSignedCreditWebhook_GrantsExactlyOnce_AndLedgerCompletesOnce()
    {
        RequirePostgres();
        var email = $"credit-race-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);
        var eventId = $"evt_credit_race_{Guid.NewGuid():N}";
        var sessionId = $"cs_credit_race_{Guid.NewGuid():N}";
        var payload = StripeEvent(eventId, "checkout.session.completed", new
        {
            @object = "checkout.session", id = sessionId, client_reference_id = $"{userId}:credits:3",
            amount_total = 2400, currency = "usd", payment_status = "paid", payment_intent = $"pi_{sessionId}",
        });

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PostPlatformAsync(payload)));
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        (await db.ReleaseCreditPurchases.Where(x => x.CreatorId == userId).ToListAsync()).Should().ContainSingle(x => x.Credits == 3);
        (await db.StripeWebhookEvents.Where(x => x.EventId == eventId).ToListAsync()).Should().ContainSingle(x => x.Status == "completed");
    }

    [Fact]
    public async Task SignedSubscriptionLifecycle_EnforcesFailedPaidUpdatedAndDeletedStates()
    {
        RequirePostgres();
        var email = $"lifecycle-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);
        var customerId = $"cus_{Guid.NewGuid():N}";
        var subscriptionId = $"sub_{Guid.NewGuid():N}";
        await SeedSubscriptionAsync(userId, customerId, subscriptionId);

        var failedInvoice = InvoiceEvent("invoice.payment_failed", customerId, subscriptionId, $"in_{Guid.NewGuid():N}");
        (await PostPlatformAsync(failedInvoice)).StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertSubscriptionStateAsync(userId, "past_due", CreatorTier.Free);

        var periodEnd = DateTimeOffset.UtcNow.AddMonths(1).ToUnixTimeSeconds();
        var updated = StripeEvent($"evt_{Guid.NewGuid():N}", "customer.subscription.updated", new
        {
            @object = "subscription", id = subscriptionId, customer = customerId, status = "active",
            current_period_end = periodEnd,
            items = new { data = new[] { new { price = new { id = "price_test_creator" } } } },
        });
        (await PostPlatformAsync(updated)).StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertSubscriptionStateAsync(userId, "active", CreatorTier.Creator);

        var paidInvoice = InvoiceEvent("invoice.paid", customerId, subscriptionId, $"in_{Guid.NewGuid():N}", periodEnd);
        (await PostPlatformAsync(paidInvoice)).StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertSubscriptionStateAsync(userId, "active", CreatorTier.Creator);

        var deleted = StripeEvent($"evt_{Guid.NewGuid():N}", "customer.subscription.deleted", new
        {
            @object = "subscription", id = subscriptionId, customer = customerId, status = "canceled",
        });
        (await PostPlatformAsync(deleted)).StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertSubscriptionStateAsync(userId, "cancelled", CreatorTier.Free);
    }

    [Fact]
    public async Task SignedAuthorshipCheckout_IsOwnerPriceBound_Idempotent_AndRefundRevokesRecord()
    {
        RequirePostgres();
        var email = $"authorship-pay-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);
        var trackId = await _fixture.SeedTrackAsync(userId, "Paid Authorship Track");
        var recordId = Guid.NewGuid();
        var sessionId = $"cs_auth_{Guid.NewGuid():N}";
        var paymentIntentId = $"pi_auth_{Guid.NewGuid():N}";
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            db.AuthorshipRecords.Add(new AuthorshipRecord
            {
                Id = recordId, TrackId = trackId, CreatorId = userId, ArtistName = "Test Artist",
                EvidenceJson = "{}", Status = "pending_payment", PaymentStatus = "pending", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        string Checkout(string eventId, long amount) => StripeEvent(eventId, "checkout.session.completed", new
        {
            @object = "checkout.session", id = sessionId, client_reference_id = $"{userId}:authorship:{recordId}",
            amount_total = amount, currency = "usd", payment_status = "paid", payment_intent = paymentIntentId,
        });

        (await PostPlatformAsync(Checkout($"evt_{Guid.NewGuid():N}", 1))).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var concurrent = await Task.WhenAll(
            PostPlatformAsync(Checkout($"evt_{Guid.NewGuid():N}", 1000)),
            PostPlatformAsync(Checkout($"evt_{Guid.NewGuid():N}", 1000)));
        concurrent.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var issued = await db.AuthorshipRecords.SingleAsync(x => x.Id == recordId);
            issued.Status.Should().Be("issued");
            issued.StripeSessionId.Should().Be(sessionId);
            issued.StripePaymentIntentId.Should().Be(paymentIntentId);
        }

        var refund = StripeEvent($"evt_{Guid.NewGuid():N}", "charge.refunded", new
        {
            @object = "charge", id = $"ch_{Guid.NewGuid():N}", payment_intent = paymentIntentId,
            amount_refunded = 1000, refunded = true, currency = "usd",
        });
        (await PostPlatformAsync(refund)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var verifyScope = _fixture.Services.CreateScope();
        (await verifyScope.ServiceProvider.GetRequiredService<CambrianDbContext>()
            .AuthorshipRecords.SingleAsync(x => x.Id == recordId)).Status.Should().Be("refunded");

        var dispute = StripeEvent($"evt_{Guid.NewGuid():N}", "charge.dispute.created", new
        {
            @object = "dispute", id = $"dp_{Guid.NewGuid():N}", payment_intent = paymentIntentId,
            charge = $"ch_{Guid.NewGuid():N}", currency = "usd",
        });
        (await PostPlatformAsync(dispute)).StatusCode.Should().Be(HttpStatusCode.OK);
        verifyScope.ServiceProvider.GetRequiredService<CambrianDbContext>().ChangeTracker.Clear();
        (await verifyScope.ServiceProvider.GetRequiredService<CambrianDbContext>()
            .AuthorshipRecords.SingleAsync(x => x.Id == recordId)).Status.Should().Be("disputed");
    }

    [Fact]
    public async Task ConcurrentSignedConnectDelivery_RecordsOneEarning_AndTracksFailedLifecycle()
    {
        RequirePostgres();
        var artistEmail = $"connect-artist-{Guid.NewGuid():N}@test.com";
        var fanEmail = $"connect-fan-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(artistEmail);
        await _fixture.RegisterUserAsync(fanEmail);
        var artistId = await _fixture.GetUserIdAsync(artistEmail);
        var fanId = await _fixture.GetUserIdAsync(fanEmail);
        var accountId = $"acct_{Guid.NewGuid():N}";
        var fanSubId = Guid.NewGuid();
        var stripeSubscriptionId = $"sub_fan_{Guid.NewGuid():N}";
        var sessionId = $"cs_fan_{Guid.NewGuid():N}";
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            (await db.Users.FindAsync(artistId))!.StripeAccountId = accountId;
            db.FanSubscriptions.Add(new FanSubscription
            {
                Id = fanSubId, FanUserId = fanId, ArtistUserId = artistId,
                PriceCents = 500, Status = "pending", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var eventId = $"evt_connect_{Guid.NewGuid():N}";
        var checkout = StripeEvent(eventId, "checkout.session.completed", new
        {
            @object = "checkout.session", id = sessionId, client_reference_id = $"{fanId}:fansub:{fanSubId}",
            amount_total = 500, currency = "usd", payment_status = "paid", subscription = stripeSubscriptionId,
        }, accountId);
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostConnectAsync(checkout)));
        var errors = await Task.WhenAll(responses.Where(r => r.StatusCode != HttpStatusCode.OK)
            .Select(async r => $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}"));
        using (var errorScope = _fixture.Services.CreateScope())
        {
            var errorDb = errorScope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var ledgerError = await errorDb.StripeWebhookEvents.AsNoTracking()
                .Where(x => x.EventId == eventId).Select(x => x.ErrorMessage).SingleOrDefaultAsync();
            errors.Should().BeEmpty($"ledger error: {ledgerError}");
        }

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            (await db.EarningsTransactions.Where(x => x.ExternalRef == sessionId).ToListAsync()).Should().ContainSingle();
            (await db.FanSubscriptions.FindAsync(fanSubId))!.Status.Should().Be("active");
        }

        var failedInvoice = StripeEvent($"evt_{Guid.NewGuid():N}", "invoice.payment_failed", new
        {
            @object = "invoice", id = $"in_{Guid.NewGuid():N}", subscription = stripeSubscriptionId,
        }, accountId);
        (await PostConnectAsync(failedInvoice)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var verifyScope = _fixture.Services.CreateScope();
        (await verifyScope.ServiceProvider.GetRequiredService<CambrianDbContext>()
            .FanSubscriptions.FindAsync(fanSubId))!.Status.Should().Be("past_due");
    }

    private async Task<HttpResponseMessage> PostPlatformAsync(string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhook/stripe")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", Sign(payload, SignedRelationalStripeWebhookApiFixture.PlatformWebhookSecret));
        return await _fixture.CreateClient().SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostConnectAsync(string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhook/stripe/connect")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", Sign(payload, SignedRelationalStripeWebhookApiFixture.ConnectWebhookSecret));
        return await _fixture.CreateClient().SendAsync(request);
    }

    private static string Sign(string payload, string secret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var digest = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();
        return $"t={timestamp},v1={digest}";
    }

    private static string SubscriptionCheckoutEvent(string userId, string priceId, string eventId, string sessionId) =>
        StripeEvent(eventId, "checkout.session.completed", new
        {
            @object = "checkout.session", id = sessionId, client_reference_id = $"{userId}:subscription:creator",
            customer = $"cus_{sessionId}", payment_status = "paid", currency = "usd",
            subscription = new
            {
                id = $"sub_{sessionId}", status = "active",
                items = new { data = new[] { new { price = new { id = priceId } } } },
            },
        });

    private static string InvoiceEvent(string type, string customerId, string subscriptionId, string invoiceId, long? periodEnd = null) =>
        StripeEvent($"evt_{Guid.NewGuid():N}", type, new
        {
            @object = "invoice", id = invoiceId, customer = customerId, subscription = subscriptionId,
            lines = new
            {
                data = new[] { new { period = new { end = periodEnd ?? DateTimeOffset.UtcNow.AddMonths(1).ToUnixTimeSeconds() } } },
            },
        });

    private static string StripeEvent(string eventId, string type, object stripeObject, string? account = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["object"] = "event", ["id"] = eventId, ["type"] = type,
            ["data"] = new Dictionary<string, object> { ["object"] = stripeObject },
        };
        if (!string.IsNullOrWhiteSpace(account)) payload["account"] = account;
        return JsonSerializer.Serialize(payload);
    }

    private async Task SeedSubscriptionAsync(string userId, string customerId, string subscriptionId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.FindAsync(userId);
        user!.Tier = "creator"; user.CreatorTier = CreatorTier.Creator; user.SubscriptionStatus = "Active";
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(), UserId = userId, Plan = "creator", Status = "active",
            StripeCustomerId = customerId, StripeSubscriptionId = subscriptionId,
            StripeSessionId = $"cs_{Guid.NewGuid():N}", StartedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMonths(1),
        });
        await db.SaveChangesAsync();
    }

    private async Task AssertSubscriptionStateAsync(string userId, string status, CreatorTier tier)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        (await db.Subscriptions.SingleAsync(s => s.UserId == userId)).Status.Should().Be(status);
        (await db.Users.FindAsync(userId))!.CreatorTier.Should().Be(tier);
    }

    private void RequirePostgres() => _fixture.DatabaseProvider.Should().Be("PostgreSQL",
        "signed payment fulfillment and concurrency tests must exercise PostgreSQL, not the SQLite fallback");
}
