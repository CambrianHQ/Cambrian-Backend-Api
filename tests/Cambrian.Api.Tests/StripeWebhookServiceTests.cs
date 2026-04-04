using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Stripe;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

public sealed class StripeWebhookServiceTests : IDisposable
{
    private readonly CambrianDbContext _db;
    private readonly ILogger<StripeWebhookService> _logger = Substitute.For<ILogger<StripeWebhookService>>();
    private readonly ILicenseService _licenseService = Substitute.For<ILicenseService>();
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

        return new StripeWebhookService(_db, _licenseService, _emailService, config, _logger, env);
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

        var svc = new StripeWebhookService(_db, _licenseService, _emailService, config, _logger, env);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.HandleStripeAsync("{}", ""));

        Assert.Contains("signature verification", ex.Message);
    }

    // ════════════════════════════════════════════════════════════════
    // H4: Persist-first pattern verification
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessEventAsync_CompletedEvent_HasCorrectStatusAndFlags()
    {
        var svc = CreateService();
        var eventId = UniqueEventId();

        await svc.ProcessEventAsync(
            eventId: eventId,
            eventType: "checkout.session.completed",
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

    [Fact]
    public async Task ProcessEventAsync_FailedEvent_RecordsErrorMessage()
    {
        // Exclusive purchase on InMemory DB triggers InvalidOperationException
        // because ExecuteSqlInterpolatedAsync is not supported.
        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track { Id = trackId, Title = "Fail Beat", CreatorId = "c1" });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        var eventId = UniqueEventId();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.ProcessEventAsync(
                eventId: eventId,
                eventType: "checkout.session.completed",
                clientReferenceId: $"user1:{trackId}:exclusive",
                amountTotal: 5000,
                stripeCustomerId: null,
                stripeSessionId: null));

        var evt = await _db.StripeWebhookEvents.FirstOrDefaultAsync(e => e.EventId == eventId);
        Assert.NotNull(evt);
        Assert.Equal("failed", evt.Status);
        Assert.False(evt.Processed);
        Assert.NotNull(evt.ErrorMessage);
    }

    // ════════════════════════════════════════════════════════════════
    // checkout.session.completed — track purchases
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessEventAsync_CheckoutCompleted_CreatesPurchaseAndLibraryItem()
    {
        var trackId = Guid.NewGuid();
        var userId = "user-1";
        _db.Tracks.Add(new Track { Id = trackId, Title = "Test Beat", CreatorId = "creator-1" });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:{trackId}:non-exclusive",
            amountTotal: 2999,
            stripeCustomerId: null,
            stripeSessionId: null);

        var purchase = await _db.Purchases.FirstOrDefaultAsync(p => p.BuyerId == userId);
        Assert.NotNull(purchase);
        Assert.Equal("completed", purchase.Status);
        Assert.Equal("non-exclusive", purchase.LicenseType);
        Assert.Equal(2999, purchase.AmountCents);
        Assert.Equal(trackId, purchase.TrackId);

        var lib = await _db.Library.FirstOrDefaultAsync(l => l.UserId == userId);
        Assert.NotNull(lib);
        Assert.Equal(trackId, lib.TrackId);
    }

    [Fact]
    public async Task ProcessEventAsync_LegacyPurchaseGuid_MarksCompleted()
    {
        var purchaseId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        _db.Purchases.Add(new Purchase
        {
            Id = purchaseId,
            BuyerId = "buyer-1",
            TrackId = trackId,
            AmountCents = 1000,
            Status = "pending"
        });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "checkout.session.completed",
            clientReferenceId: purchaseId.ToString(),
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: null);

        var purchase = await _db.Purchases.FindAsync(purchaseId);
        Assert.Equal("completed", purchase!.Status);
    }

    [Fact]
    public async Task ProcessEventAsync_NullClientReferenceId_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "checkout.session.completed",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: null);
        // No exception = handled gracefully when clientReferenceId is null
    }

    // ── Duplicate purchase prevention ──

    [Fact]
    public async Task ProcessEventAsync_DuplicatePurchase_DoesNotCreateSecondRecord()
    {
        var trackId = Guid.NewGuid();
        var userId = "user-dup";
        _db.Tracks.Add(new Track { Id = trackId, Title = "Dup Beat", CreatorId = "creator-1" });
        _db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = userId,
            TrackId = trackId,
            AmountCents = 1000,
            LicenseType = "non-exclusive",
            Status = "pending"
        });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:{trackId}:non-exclusive",
            amountTotal: 1000,
            stripeCustomerId: null,
            stripeSessionId: null);

        var purchases = await _db.Purchases
            .Where(p => p.BuyerId == userId && p.TrackId == trackId)
            .ToListAsync();
        Assert.Single(purchases);
        Assert.Equal("completed", purchases[0].Status);
    }

    [Fact]
    public async Task ProcessEventAsync_ReplayedEventId_DoesNotReprocessTrackPurchase()
    {
        var trackId = Guid.NewGuid();
        var userId = "user-replay";
        _db.Tracks.Add(new Track { Id = trackId, Title = "Replay Beat", CreatorId = "creator-1" });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        var eventId = "evt_replay_purchase";

        await svc.ProcessEventAsync(
            eventId: eventId,
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:{trackId}:non-exclusive",
            amountTotal: 2500,
            stripeCustomerId: null,
            stripeSessionId: null);

        // Second call with same eventId should be idempotent
        await svc.ProcessEventAsync(
            eventId: eventId,
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:{trackId}:non-exclusive",
            amountTotal: 2500,
            stripeCustomerId: null,
            stripeSessionId: null);

        var purchases = await _db.Purchases
            .Where(p => p.BuyerId == userId && p.TrackId == trackId)
            .ToListAsync();
        var libraryItems = await _db.Library
            .Where(l => l.UserId == userId && l.TrackId == trackId)
            .ToListAsync();

        Assert.Single(purchases);
        Assert.Equal(2500, purchases[0].AmountCents);
        Assert.Single(libraryItems);
        Assert.Single(await _db.StripeWebhookEvents.ToListAsync());
    }

    // ── Dead-letter logging for invalid references ──

    [Fact]
    public async Task ProcessEventAsync_InvalidTrackId_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "checkout.session.completed",
            clientReferenceId: "user1:not-a-guid:non-exclusive",
            amountTotal: 999,
            stripeCustomerId: null,
            stripeSessionId: null);

        var purchases = await _db.Purchases.ToListAsync();
        Assert.Empty(purchases);
    }

    [Fact]
    public async Task ProcessEventAsync_MissingTrack_DoesNotThrow()
    {
        var missingTrackId = Guid.NewGuid();
        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "checkout.session.completed",
            clientReferenceId: $"user1:{missingTrackId}:non-exclusive",
            amountTotal: 999,
            stripeCustomerId: null,
            stripeSessionId: null);

        var purchases = await _db.Purchases.ToListAsync();
        Assert.Empty(purchases);
    }

    // ════════════════════════════════════════════════════════════════
    // checkout.session.completed — subscription upgrades
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
            stripeSessionId: null);

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
            stripeSessionId: null);

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
    public async Task ProcessEventAsync_ReplayedEventId_DoesNotCreateSecondSubscription()
    {
        var userId = "user-replay-sub";
        _db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, Tier = "free" });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        var eventId = "evt_replay_subscription";

        await svc.ProcessEventAsync(
            eventId: eventId,
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:subscription:creator",
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: null);

        // Second call with same eventId
        await svc.ProcessEventAsync(
            eventId: eventId,
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:subscription:creator",
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: null);

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
    // Subscription lifecycle webhooks
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

    // ════════════════════════════════════════════════════════════════
    // License certificate creation
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessEventAsync_CheckoutCompleted_IssuesLicenseCertificate()
    {
        var trackId = Guid.NewGuid();
        var userId = "user-license";
        var licenseId = Guid.NewGuid();
        _db.Tracks.Add(new Track { Id = trackId, Title = "License Beat", CreatorId = "creator-lic" });
        await _db.SaveChangesAsync();

        _licenseService.IssueCertificateAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new Cambrian.Application.DTOs.Licenses.LicenseCertificateDto
            {
                LicenseId = licenseId.ToString(),
                TrackId = trackId.ToString(),
                BuyerId = userId,
                CreatorId = "creator-lic",
                UsageType = "personal",
                IssuedAt = DateTime.UtcNow
            });

        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:{trackId}:non-exclusive:personal",
            amountTotal: 1999,
            stripeCustomerId: null,
            stripeSessionId: null);

        var purchase = await _db.Purchases.FirstOrDefaultAsync(p => p.BuyerId == userId && p.TrackId == trackId);
        Assert.NotNull(purchase);
        Assert.Equal(licenseId, purchase.LicenseId);

        await _licenseService.Received(1).IssueCertificateAsync(
            purchase.Id,
            Arg.Any<string>(),
            userId,
            "creator-lic",
            "non-exclusive",
            "personal");
    }

    // ── Duplicate purchase ensures library exists ──

    [Fact]
    public async Task ProcessEventAsync_DuplicatePurchase_BackfillsLibraryItem()
    {
        var trackId = Guid.NewGuid();
        var userId = "user-dup-lib";
        var purchaseId = Guid.NewGuid();
        _db.Tracks.Add(new Track { Id = trackId, Title = "Dup Lib Beat", CreatorId = "creator-dup" });
        _db.Purchases.Add(new Purchase
        {
            Id = purchaseId,
            BuyerId = userId,
            TrackId = trackId,
            AmountCents = 1500,
            LicenseType = "non-exclusive",
            Status = "completed"
        });
        // Intentionally NO library item — simulating a gap
        await _db.SaveChangesAsync();

        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:{trackId}:non-exclusive",
            amountTotal: 1500,
            stripeCustomerId: null,
            stripeSessionId: null);

        // Purchase should still be single
        var purchases = await _db.Purchases
            .Where(p => p.BuyerId == userId && p.TrackId == trackId)
            .ToListAsync();
        Assert.Single(purchases);

        // Library item should now exist (back-filled)
        var lib = await _db.Library
            .FirstOrDefaultAsync(l => l.UserId == userId && l.TrackId == trackId);
        Assert.NotNull(lib);
        Assert.Equal(purchaseId, lib.PurchaseId);
    }

    // ── License failure does not block purchase+library ──

    [Fact]
    public async Task ProcessEventAsync_LicenseFailure_StillCreatesPurchaseAndLibrary()
    {
        var trackId = Guid.NewGuid();
        var userId = "user-lic-fail";
        _db.Tracks.Add(new Track { Id = trackId, Title = "Fail License Beat", CreatorId = "creator-fail" });
        await _db.SaveChangesAsync();

        _licenseService.IssueCertificateAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .ThrowsAsync(new Exception("License service unavailable"));

        var svc = CreateService();
        await svc.ProcessEventAsync(
            eventId: UniqueEventId(),
            eventType: "checkout.session.completed",
            clientReferenceId: $"{userId}:{trackId}:non-exclusive",
            amountTotal: 2000,
            stripeCustomerId: null,
            stripeSessionId: null);

        // Purchase should still be created
        var purchase = await _db.Purchases.FirstOrDefaultAsync(p => p.BuyerId == userId && p.TrackId == trackId);
        Assert.NotNull(purchase);
        Assert.Equal("completed", purchase.Status);

        // Library should still be created
        var lib = await _db.Library.FirstOrDefaultAsync(l => l.UserId == userId && l.TrackId == trackId);
        Assert.NotNull(lib);

        // License should NOT be linked (it failed)
        Assert.Null(purchase.LicenseId);
    }
}
