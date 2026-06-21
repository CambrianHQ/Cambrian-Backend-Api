using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Stripe;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class StripeWebhookServiceTests : IDisposable
{
    private readonly CambrianDbContext _db;
    private readonly ILogger<StripeWebhookService> _logger = Substitute.For<ILogger<StripeWebhookService>>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();

    public StripeWebhookServiceTests()
    {
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new CambrianDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private StripeWebhookService CreateService(string webhookSecret = "whsec_test")
    {
        var config = Substitute.For<IConfiguration>();
        config["Stripe:WebhookSecret"].Returns(webhookSecret);

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        return new StripeWebhookService(_db, _emailService, config, _logger, env);
    }

    private StripeWebhookService CreateServiceWithPrices(string creatorPrice, string proPrice)
    {
        var config = Substitute.For<IConfiguration>();
        config["Stripe:WebhookSecret"].Returns("whsec_test");
        config["Stripe:Prices:Creator"].Returns(creatorPrice);
        config["Stripe:Prices:Pro"].Returns(proPrice);

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        return new StripeWebhookService(_db, _emailService, config, _logger, env);
    }

    private static string UniqueEventId() => $"evt_{Guid.NewGuid():N}";

    // ════════════════════════════════════════════════════════════════
    // C4: Signature verification — always required, no dev bypass
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HandleStripeAsync_ThrowsWhenWebhookSecretMissing()
    {
        var svc = CreateService(webhookSecret: "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.HandleStripeAsync("{}", ""));

        Assert.Contains("signature verification", ex.Message);
    }

    [Fact]
    public async Task HandleStripeAsync_ThrowsWhenSignatureMissing()
    {
        var svc = CreateService(webhookSecret: "whsec_test");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.HandleStripeAsync("{}", ""));

        Assert.Contains("signature verification", ex.Message);
    }

    [Fact]
    public async Task HandleStripeAsync_ThrowsWhenSecretMissingButSignaturePresent()
    {
        var svc = CreateService(webhookSecret: "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.HandleStripeAsync("{}", "sig_123"));

        Assert.Contains("signature verification", ex.Message);
    }

    [Fact]
    public async Task HandleStripeAsync_DevMode_StillRequiresSignatureVerification()
    {
        // C4: Development mode no longer bypasses signature verification
        var config = Substitute.For<IConfiguration>();
        config["Stripe:WebhookSecret"].Returns("");

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        var svc = new StripeWebhookService(_db, _emailService, config, _logger, env);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.HandleStripeAsync("{}", ""));

        Assert.Contains("signature verification", ex.Message);
    }

    // ════════════════════════════════════════════════════════════════
    // Persist-first pattern + idempotency ledger
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessEventAsync_CompletedEvent_HasCorrectStatusAndFlags()
    {
        var svc = CreateService();
        var eventId = UniqueEventId();

        await svc.ProcessEventAsync(
            eventId: eventId,
            eventType: "customer.created",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: null);

        var evt = await _db.StripeWebhookEvents.FirstOrDefaultAsync(e => e.EventId == eventId);
        Assert.NotNull(evt);
        Assert.Equal("completed", evt.Status);
        Assert.True(evt.Processed);
        Assert.Null(evt.ErrorMessage);
    }

    // ════════════════════════════════════════════════════════════════
    // checkout.session.completed — track-license purchasing has been REMOVED
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessEventAsync_TrackPurchaseReference_CreatesNoPurchaseLibraryOrWalletCredit()
    {
        // REGRESSION (licensing removal): a legacy track-purchase clientReferenceId
        // ("userId:trackId:licenseType") must no longer create a Purchase, Library item,
        // or creator wallet credit, and must not issue any license. Only an [IGNORED]
        // dead-letter log fires; the event still completes so Stripe receives 200.
        var trackId = Guid.NewGuid();
        var userId = "buyer-x";
        _db.Tracks.Add(new Track
        {
            Id = trackId,
            Title = "Beat",
            CreatorId = "creator-x",
            NonExclusivePriceCents = 2999
        });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        var eventId = UniqueEventId();

        await svc.ProcessEventAsync(
            eventId: eventId,
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:{trackId}:non-exclusive",
            amountTotal: 2999,
            stripeCustomerId: null,
            stripeSessionId: null);

        Assert.Empty(await _db.Purchases.ToListAsync());
        Assert.Empty(await _db.Library.ToListAsync());
        Assert.Empty(await _db.WalletTransactions.ToListAsync());

        // Event still completes (no 5xx → no Stripe retry storm).
        var evt = await _db.StripeWebhookEvents.FirstOrDefaultAsync(e => e.EventId == eventId);
        Assert.NotNull(evt);
        Assert.Equal("completed", evt.Status);
        Assert.True(evt.Processed);
    }

    [Fact]
    public async Task ProcessEventAsync_NullClientReferenceId_RemainsFailedForRetry()
    {
        var svc = CreateService();
        var eventId = UniqueEventId();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ProcessEventAsync(
            eventId: eventId,
            eventType: "checkout.session.completed",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: "cs_missing_ref"));

        var evt = await _db.StripeWebhookEvents.SingleAsync(e => e.EventId == eventId);
        Assert.Equal("failed", evt.Status);
        Assert.False(evt.Processed);
    }

    [Fact]
    public async Task ProcessEventAsync_NonSubscriptionReference_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "checkout.session.completed",
            clientReferenceId: $"user1:{Guid.NewGuid()}:non-exclusive",
            amountTotal: 999,
            stripeCustomerId: null,
            stripeSessionId: null);

        Assert.Empty(await _db.Purchases.ToListAsync());
    }

    // ════════════════════════════════════════════════════════════════
    // checkout.session.completed — subscription upgrades (PRESERVED)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessEventAsync_SubscriptionCheckout_UpgradesTier()
    {
        var userId = "user-sub";
        _db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, Tier = "free" });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:subscription:creator",
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: "cs_subscription_upgrade");

        var user = await _db.Users.FindAsync(userId);
        Assert.Equal("creator", user!.Tier);

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId);
        Assert.NotNull(sub);
        Assert.Equal("active", sub.Status);
        Assert.Equal("creator", sub.Plan);
    }

    [Fact]
    public async Task ProcessEventAsync_SubscriptionUpgrade_CancelsExisting()
    {
        var userId = "user-upgrade";
        _db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, Tier = "paid" });
        _db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Plan = "paid",
            Status = "active",
            StartedAt = DateTime.UtcNow.AddMonths(-1)
        });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:subscription:creator",
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: "cs_subscription_existing_upgrade");

        var subs = await _db.Subscriptions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.StartedAt)
            .ToListAsync();
        Assert.Equal(2, subs.Count);
        Assert.Equal("active", subs[0].Status);
        Assert.Equal("creator", subs[0].Plan);
        Assert.Equal("cancelled", subs[1].Status);

        var user = await _db.Users.FindAsync(userId);
        Assert.Equal("creator", user!.Tier);
    }

    [Fact]
    public async Task ProcessEventAsync_SubscriptionUpdated_SyncsTierStatusAndPeriodEnd()
    {
        // Portal-driven upgrade creator -> pro arrives as customer.subscription.updated.
        var userId = "user-portal-upgrade";
        const string customerId = "cus_portal_1";
        _db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, Tier = "creator", CreatorTier = Cambrian.Domain.Enums.CreatorTier.Creator });
        _db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Plan = "creator",
            Status = "active",
            StripeCustomerId = customerId,
            StartedAt = DateTime.UtcNow.AddDays(-5),
            ExpiresAt = DateTime.UtcNow.AddDays(25)
        });
        await _db.SaveChangesAsync();

        var periodEnd = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        var payload = $$"""
        {
          "type": "customer.subscription.updated",
          "data": { "object": {
            "customer": "{{customerId}}",
            "status": "active",
            "current_period_end": {{periodEnd}},
            "items": { "data": [ { "price": { "id": "price_pro_live" } } ] }
          } }
        }
        """;

        var svc = CreateServiceWithPrices(creatorPrice: "price_creator_live", proPrice: "price_pro_live");
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "customer.subscription.updated",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: customerId,
            stripeSessionId: null,
            stripePaymentIntentId: null,
            payload: payload,
            stripeSubscriptionId: "sub_portal_1");

        var user = await _db.Users.FindAsync(userId);
        Assert.Equal("pro", user!.Tier);
        Assert.Equal(Cambrian.Domain.Enums.CreatorTier.Pro, user.CreatorTier);
        Assert.Equal("Active", user.SubscriptionStatus);

        var sub = await _db.Subscriptions.FirstAsync(s => s.UserId == userId);
        Assert.Equal("pro", sub.Plan);
        Assert.Equal("active", sub.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(periodEnd).UtcDateTime, sub.ExpiresAt);
    }

    [Fact]
    public async Task ProcessEventAsync_ReplayedEventId_DoesNotCreateSecondSubscription()
    {
        var userId = "user-replay-sub";
        _db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, Tier = "free" });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        var eventId = "evt_replay_subscription";
        const string sessionId = "cs_replay_subscription";

        await svc.ProcessEventAsync(
            eventId: eventId,
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:subscription:creator",
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: sessionId);

        // Second call with same eventId
        await svc.ProcessEventAsync(
            eventId: eventId,
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:subscription:creator",
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: sessionId);

        var subscriptions = await _db.Subscriptions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.StartedAt)
            .ToListAsync();

        Assert.Single(subscriptions);
        Assert.Equal("active", subscriptions[0].Status);
        Assert.Equal("creator", subscriptions[0].Plan);
        Assert.Single(await _db.StripeWebhookEvents.ToListAsync());
    }

    // ════════════════════════════════════════════════════════════════
    // Subscription lifecycle webhooks (PRESERVED)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessEventAsync_SubscriptionDeleted_HandlesGracefully()
    {
        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "customer.subscription.deleted",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: "cus_test_123",
            stripeSessionId: null);
        // No exception = handled gracefully; logs warning about manual review
    }

    [Fact]
    public async Task ProcessEventAsync_SubscriptionDeleted_NoCustomerId_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "customer.subscription.deleted",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: null);
        // No exception = handles missing customer ID gracefully
    }

    // ── Audit gap closer: cancel/downgrade must REVOKE the paid entitlement ──
    // Pre-existing tests only proved "did not throw". These assert the actual
    // state change: a cancelled/downgraded subscriber loses paid entitlement.

    [Fact]
    public async Task ProcessEventAsync_SubscriptionDeleted_DowngradesProToFree_AndRevokesPaidEntitlement()
    {
        // A Pro subscriber whose Stripe subscription is cancelled (customer.subscription.deleted).
        const string userId = "user-cancel-pro";
        const string customerId = "cus_cancel_pro";
        _db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = userId,
            Tier = "pro",
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro,
            SubscriptionStatus = "Active",
        });
        // The webhook resolves the user via an active subscription carrying the Stripe
        // customer id (so the test never falls back to a live Stripe lookup).
        _db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Plan = "pro",
            Status = "active",
            StripeCustomerId = customerId,
            StartedAt = DateTime.UtcNow.AddDays(-10),
            ExpiresAt = DateTime.UtcNow.AddDays(20),
        });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "customer.subscription.deleted",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: customerId,
            stripeSessionId: null);

        // Tier is reset to free — both the string field and the authoritative enum —
        // and the subscription is marked cancelled.
        var user = await _db.Users.FindAsync(userId);
        Assert.Equal("free", user!.Tier);
        Assert.Equal(Cambrian.Domain.Enums.CreatorTier.Free, user.CreatorTier);
        Assert.Equal("Cancelled", user.SubscriptionStatus);

        var sub = await _db.Subscriptions.FirstAsync(s => s.UserId == userId);
        Assert.Equal("cancelled", sub.Status);

        // Entitlement revocation is the real invariant: the resolved tier now grants
        // ZERO Release Ready credits and no paid/Pro-only capabilities — proving the
        // cancel strips entitlement rather than merely relabelling the user.
        var resolved = Cambrian.Application.Configuration.TierManifest.For(user.CreatorTier);
        Assert.Equal(0, resolved.ReleaseReadyCreditsPerMonth);
        Assert.False(resolved.FeatureFlags["apiAccess"], "a cancelled subscriber loses Pro-only entitlements");
        Assert.False(resolved.FeatureFlags["unlimitedTracks"], "a cancelled subscriber loses paid entitlements");
    }

    [Fact]
    public async Task ProcessEventAsync_SubscriptionUpdated_DowngradeProToCreator_LowersTierAndRevokesProEntitlement()
    {
        // Portal-driven DOWNGRADE pro -> creator arrives as customer.subscription.updated
        // carrying the Creator price id.
        const string userId = "user-downgrade-pro";
        const string customerId = "cus_downgrade_pro";
        _db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = userId,
            Tier = "pro",
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro,
            SubscriptionStatus = "Active",
        });
        _db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Plan = "pro",
            Status = "active",
            StripeCustomerId = customerId,
            StartedAt = DateTime.UtcNow.AddDays(-5),
            ExpiresAt = DateTime.UtcNow.AddDays(25),
        });
        await _db.SaveChangesAsync();

        var periodEnd = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        var payload = $$"""
        {
          "type": "customer.subscription.updated",
          "data": { "object": {
            "customer": "{{customerId}}",
            "status": "active",
            "current_period_end": {{periodEnd}},
            "items": { "data": [ { "price": { "id": "price_creator_live" } } ] }
          } }
        }
        """;

        var svc = CreateServiceWithPrices(creatorPrice: "price_creator_live", proPrice: "price_pro_live");
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "customer.subscription.updated",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: customerId,
            stripeSessionId: null,
            stripePaymentIntentId: null,
            payload: payload,
            stripeSubscriptionId: "sub_downgrade_1");

        // Tier drops pro -> creator (string + authoritative enum).
        var user = await _db.Users.FindAsync(userId);
        Assert.Equal("creator", user!.Tier);
        Assert.Equal(Cambrian.Domain.Enums.CreatorTier.Creator, user.CreatorTier);

        var sub = await _db.Subscriptions.FirstAsync(s => s.UserId == userId);
        Assert.Equal("creator", sub.Plan);

        // Pro-only entitlement is revoked while Creator entitlement remains, and the
        // Release Ready grant falls from the Pro allowance (10) to the Creator allowance (3).
        var resolved = Cambrian.Application.Configuration.TierManifest.For(user.CreatorTier);
        Assert.Equal(3, resolved.ReleaseReadyCreditsPerMonth);
        Assert.False(resolved.FeatureFlags["apiAccess"], "Pro-only entitlement is revoked on downgrade");
        Assert.True(resolved.FeatureFlags["unlimitedTracks"], "Creator-tier entitlements are retained");
    }

    [Fact]
    public async Task ProcessEventAsync_InvoicePaymentFailed_HandlesGracefully()
    {
        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "invoice.payment_failed",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: "cus_test_456",
            stripeSessionId: null);
        // No exception = handled gracefully; logs warning about retry
    }

    [Fact]
    public async Task ProcessEventAsync_InvoicePaymentFailed_NoCustomerId_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "invoice.payment_failed",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: null);
        // No exception = handles missing customer ID gracefully
    }

    [Fact]
    public async Task ProcessEventAsync_InvoicePaid_UnknownCustomer_RemainsFailedForRetry()
    {
        var svc = CreateService();
        var eventId = UniqueEventId();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.ProcessEventAsync(
            eventId: eventId,
            eventType: "invoice.paid",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: "cus_unknown_paid",
            stripeSessionId: null));

        var evt = await _db.StripeWebhookEvents.SingleAsync(e => e.EventId == eventId);
        Assert.Equal("failed", evt.Status);
        Assert.False(evt.Processed);
    }
}
