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

    public StripeWebhookServiceTests()
    {
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new CambrianDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private StripeWebhookService CreateService(
        string webhookSecret = "",
        bool isDevelopment = false)
    {
        var config = Substitute.For<IConfiguration>();
        config["Stripe:WebhookSecret"].Returns(webhookSecret);

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(isDevelopment ? "Development" : "Production");

        return new StripeWebhookService(_db, config, _logger, env);
    }

    // ── Security gate: non-Development rejection ──

    [Fact]
    public async Task HandleStripeAsync_NonDev_ThrowsWhenSecretAndSignatureBothEmpty()
    {
        var svc = CreateService(webhookSecret: "", isDevelopment: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.HandleStripeAsync("{}", ""));

        Assert.Contains("signature verification", ex.Message);
    }

    [Fact]
    public async Task HandleStripeAsync_NonDev_ThrowsWhenSecretPresentButSignatureEmpty()
    {
        var svc = CreateService(webhookSecret: "whsec_test", isDevelopment: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.HandleStripeAsync("{}", ""));

        Assert.Contains("signature verification", ex.Message);
    }

    [Fact]
    public async Task HandleStripeAsync_NonDev_ThrowsWhenSignaturePresentButSecretEmpty()
    {
        var svc = CreateService(webhookSecret: "", isDevelopment: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.HandleStripeAsync("{}", "sig_123"));

        Assert.Contains("signature verification", ex.Message);
    }

    // ── Development fallback: JSON parsing without signature ──

    [Fact]
    public async Task HandleStripeAsync_Dev_ParsesJsonWithoutSignature()
    {
        var svc = CreateService(webhookSecret: "", isDevelopment: true);
        var payload = """{"type":"payment_intent.succeeded","data":{"object":{}}}""";

        await svc.HandleStripeAsync(payload, "");
        // No exception = dev fallback worked for non-checkout event
    }

    [Fact]
    public async Task HandleStripeAsync_Dev_ProcessesCheckoutSession()
    {
        var trackId = Guid.NewGuid();
        var userId = "user-1";
        _db.Tracks.Add(new Track { Id = trackId, Title = "Test Beat", CreatorId = "creator-1" });
        await _db.SaveChangesAsync();

        var svc = CreateService(webhookSecret: "", isDevelopment: true);
        var payload = $$"""
        {
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "{{userId}}:{{trackId}}:non-exclusive",
                    "amount_total": 2999
                }
            }
        }
        """;

        await svc.HandleStripeAsync(payload, "");

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
    public async Task HandleStripeAsync_Dev_SubscriptionCheckout_UpgradesTier()
    {
        var userId = "user-sub";
        _db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, Tier = "free" });
        await _db.SaveChangesAsync();

        var svc = CreateService(webhookSecret: "", isDevelopment: true);
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

        await svc.HandleStripeAsync(payload, "");

        var user = await _db.Users.FindAsync(userId);
        Assert.Equal("creator", user!.Tier);

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId);
        Assert.NotNull(sub);
        Assert.Equal("active", sub.Status);
        Assert.Equal("creator", sub.Plan);
    }

    // ── HandleCheckoutCompleted routing: legacy GUID path ──

    [Fact]
    public async Task HandleStripeAsync_Dev_LegacyPurchaseGuid_MarksCompleted()
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

        var svc = CreateService(webhookSecret: "", isDevelopment: true);
        var payload = $$"""
        {
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "{{purchaseId}}"
                }
            }
        }
        """;

        await svc.HandleStripeAsync(payload, "");

        var purchase = await _db.Purchases.FindAsync(purchaseId);
        Assert.Equal("completed", purchase!.Status);
    }

    [Fact]
    public async Task HandleStripeAsync_Dev_NullClientReferenceId_DoesNotThrow()
    {
        var svc = CreateService(webhookSecret: "", isDevelopment: true);
        var payload = """
        {
            "type": "checkout.session.completed",
            "data": {
                "object": {}
            }
        }
        """;

        await svc.HandleStripeAsync(payload, "");
        // No exception = handled gracefully when clientReferenceId is null
    }

    // ── Duplicate purchase prevention ──

    [Fact]
    public async Task HandleStripeAsync_Dev_DuplicatePurchase_DoesNotCreateSecondRecord()
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

        var svc = CreateService(webhookSecret: "", isDevelopment: true);
        var payload = $$"""
        {
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "{{userId}}:{{trackId}}:non-exclusive",
                    "amount_total": 1000
                }
            }
        }
        """;

        await svc.HandleStripeAsync(payload, "");

        var purchases = await _db.Purchases
            .Where(p => p.BuyerId == userId && p.TrackId == trackId)
            .ToListAsync();
        Assert.Single(purchases);
        Assert.Equal("completed", purchases[0].Status);
    }

    [Fact]
    public async Task HandleStripeAsync_Dev_ReplayedEventId_DoesNotReprocessTrackPurchase()
    {
        var trackId = Guid.NewGuid();
        var userId = "user-replay";
        _db.Tracks.Add(new Track { Id = trackId, Title = "Replay Beat", CreatorId = "creator-1" });
        await _db.SaveChangesAsync();

        var svc = CreateService(webhookSecret: "", isDevelopment: true);
        var payload = $$"""
        {
            "id": "evt_replay_purchase",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "{{userId}}:{{trackId}}:non-exclusive",
                    "amount_total": 2500
                }
            }
        }
        """;

        await svc.HandleStripeAsync(payload, "");
        await svc.HandleStripeAsync(payload, "");

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

    // ── Subscription upgrade cancels existing subscription ──

    [Fact]
    public async Task HandleStripeAsync_Dev_SubscriptionUpgrade_CancelsExisting()
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

        var svc = CreateService(webhookSecret: "", isDevelopment: true);
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

        await svc.HandleStripeAsync(payload, "");

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
    public async Task HandleStripeAsync_Dev_ReplayedEventId_DoesNotCreateSecondSubscription()
    {
        var userId = "user-replay-sub";
        _db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, Tier = "free" });
        await _db.SaveChangesAsync();

        var svc = CreateService(webhookSecret: "", isDevelopment: true);
        var payload = $$"""
        {
            "id": "evt_replay_subscription",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "{{userId}}:subscription:creator"
                }
            }
        }
        """;

        await svc.HandleStripeAsync(payload, "");
        await svc.HandleStripeAsync(payload, "");

        var subscriptions = await _db.Subscriptions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.StartedAt)
            .ToListAsync();

        Assert.Single(subscriptions);
        Assert.Equal("active", subscriptions[0].Status);
        Assert.Equal("creator", subscriptions[0].Plan);
        Assert.Single(await _db.StripeWebhookEvents.ToListAsync());
    }
}
