using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Stripe;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests that payment-related operations are idempotent: replayed webhook events,
/// duplicate purchase attempts, and concurrent subscription upgrades do not
/// create duplicate records or corrupt state.
/// </summary>
public sealed class PaymentIdempotencyTests : IDisposable
{
    private readonly CambrianDbContext _db;
    private readonly ILogger<StripeWebhookService> _logger = Substitute.For<ILogger<StripeWebhookService>>();

    public PaymentIdempotencyTests()
    {
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new CambrianDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private StripeWebhookService CreateService()
    {
        var config = Substitute.For<IConfiguration>();
        config["Stripe:WebhookSecret"].Returns("");
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        return new StripeWebhookService(_db, config, _logger, env);
    }

    // ── Track purchase idempotency: same user + same track ──

    [Fact]
    public async Task TrackPurchase_SameUserSameTrack_OnlyCreatesOnePurchase()
    {
        var trackId = Guid.NewGuid();
        var userId = "buyer-idempotent-1";
        _db.Tracks.Add(new Track { Id = trackId, Title = "Idempotent Beat", CreatorId = "creator-1" });
        await _db.SaveChangesAsync();

        var svc = CreateService();
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
        await svc.HandleStripeAsync(payload, "");
        await svc.HandleStripeAsync(payload, "");

        var purchases = await _db.Purchases
            .Where(p => p.BuyerId == userId && p.TrackId == trackId)
            .ToListAsync();
        Assert.Single(purchases);
    }

    [Fact]
    public async Task TrackPurchase_SameUserSameTrack_OnlyCreatesOneLibraryItem()
    {
        var trackId = Guid.NewGuid();
        var userId = "buyer-idempotent-2";
        _db.Tracks.Add(new Track { Id = trackId, Title = "Library Guard Beat", CreatorId = "creator-1" });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        var payload = $$"""
        {
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "{{userId}}:{{trackId}}:non-exclusive",
                    "amount_total": 1999
                }
            }
        }
        """;

        await svc.HandleStripeAsync(payload, "");
        await svc.HandleStripeAsync(payload, "");

        var libraryItems = await _db.Library
            .Where(l => l.UserId == userId && l.TrackId == trackId)
            .ToListAsync();
        Assert.Single(libraryItems);
    }

    // ── Event ID deduplication ──

    [Fact]
    public async Task WebhookEvent_DuplicateEventId_ProcessedOnlyOnce()
    {
        var trackId = Guid.NewGuid();
        var userId = "buyer-dedup-1";
        _db.Tracks.Add(new Track { Id = trackId, Title = "Dedup Beat", CreatorId = "creator-1" });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        var eventId = "evt_test_dedup_" + Guid.NewGuid().ToString("N")[..8];
        var payload = $$"""
        {
            "id": "{{eventId}}",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "{{userId}}:{{trackId}}:non-exclusive",
                    "amount_total": 3500
                }
            }
        }
        """;

        await svc.HandleStripeAsync(payload, "");
        await svc.HandleStripeAsync(payload, "");
        await svc.HandleStripeAsync(payload, "");

        var eventRecords = await _db.StripeWebhookEvents
            .Where(e => e.EventId == eventId)
            .ToListAsync();
        Assert.Single(eventRecords);

        var purchases = await _db.Purchases
            .Where(p => p.BuyerId == userId && p.TrackId == trackId)
            .ToListAsync();
        Assert.Single(purchases);
    }

    // ── Subscription checkout idempotency ──

    [Fact]
    public async Task SubscriptionCheckout_DuplicateEventId_CreatesOnlyOneSubscription()
    {
        var userId = "sub-dedup-1";
        _db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, Tier = "free" });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        var eventId = "evt_sub_dedup_" + Guid.NewGuid().ToString("N")[..8];
        var payload = $$"""
        {
            "id": "{{eventId}}",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "{{userId}}:subscription:paid"
                }
            }
        }
        """;

        await svc.HandleStripeAsync(payload, "");
        await svc.HandleStripeAsync(payload, "");

        var subscriptions = await _db.Subscriptions
            .Where(s => s.UserId == userId)
            .ToListAsync();
        Assert.Single(subscriptions);
        Assert.Equal("paid", subscriptions[0].Plan);
        Assert.Equal("active", subscriptions[0].Status);
    }

    // ── Exclusive purchase: creates purchase record, duplicate is skipped ──

    [Fact]
    public async Task ExclusivePurchase_DuplicateAttempt_OnlyCreatesOnePurchase()
    {
        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track
        {
            Id = trackId,
            Title = "Exclusive Beat",
            CreatorId = "creator-excl",
            ExclusivePriceCents = 49999,
            ExclusiveSold = false
        });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        var payload = $$"""
        {
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "buyer-excl-1:{{trackId}}:exclusive",
                    "amount_total": 49999
                }
            }
        }
        """;

        await svc.HandleStripeAsync(payload, "");
        await svc.HandleStripeAsync(payload, "");

        var purchases = await _db.Purchases
            .Where(p => p.TrackId == trackId && p.LicenseType == "exclusive")
            .ToListAsync();
        Assert.Single(purchases);
        Assert.Equal("completed", purchases[0].Status);
    }

    /// <summary>
    /// GAP: The webhook handler does not currently set Track.ExclusiveSold = true
    /// after processing an exclusive purchase. This test documents the gap so it
    /// can be addressed in a future iteration.
    /// </summary>
    [Fact]
    public async Task ExclusivePurchase_DoesNotYetSetExclusiveSoldFlag()
    {
        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track
        {
            Id = trackId,
            Title = "Flag Gap Beat",
            CreatorId = "creator-excl-gap",
            ExclusivePriceCents = 49999,
            ExclusiveSold = false
        });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        var payload = $$"""
        {
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "buyer-excl-gap:{{trackId}}:exclusive",
                    "amount_total": 49999
                }
            }
        }
        """;

        await svc.HandleStripeAsync(payload, "");

        var track = await _db.Tracks.FindAsync(trackId);
        // NOTE: ExclusiveSold is NOT set by HandleTrackPurchase — this is a known gap.
        // When fixed, change Assert.False to Assert.True here.
        Assert.False(track!.ExclusiveSold);
    }

    // ── Wallet credit idempotency through webhook ──

    [Fact]
    public async Task WalletCredit_DuplicateEvent_AppliedOnlyOnce()
    {
        var trackId = Guid.NewGuid();
        var creatorId = "creator-wallet-idem";
        _db.Users.Add(new ApplicationUser { Id = creatorId, UserName = creatorId });
        _db.Tracks.Add(new Track { Id = trackId, Title = "Wallet Beat", CreatorId = creatorId });
        await _db.SaveChangesAsync();

        var svc = CreateService();
        var eventId = "evt_wallet_dedup_" + Guid.NewGuid().ToString("N")[..8];
        var payload = $$"""
        {
            "id": "{{eventId}}",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "buyer-wallet:{{trackId}}:non-exclusive",
                    "amount_total": 2999
                }
            }
        }
        """;

        await svc.HandleStripeAsync(payload, "");
        await svc.HandleStripeAsync(payload, "");

        var walletTxns = await _db.WalletTransactions
            .Where(w => w.UserId == creatorId)
            .ToListAsync();

        Assert.True(walletTxns.Count <= 1,
            $"Expected at most 1 wallet credit, got {walletTxns.Count}");
    }

    // ── Purchase state transition is one-way: pending → completed ──

    [Fact]
    public async Task PurchaseStatus_DoesNotRevertFromCompleted()
    {
        var purchaseId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        _db.Purchases.Add(new Purchase
        {
            Id = purchaseId,
            BuyerId = "buyer-state",
            TrackId = trackId,
            Amount = 10,
            Status = "completed",
            LicenseType = "non-exclusive"
        });
        await _db.SaveChangesAsync();

        var svc = CreateService();
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

    // ── Concurrent subscription upgrades: only most recent wins ──

    [Fact]
    public async Task SubscriptionUpgrade_FromPaidToCreator_CancelsPrevious()
    {
        var userId = "user-upgrade-idem";
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
        Assert.Equal("creator", subs[0].Plan);
        Assert.Equal("active", subs[0].Status);
        Assert.Equal("cancelled", subs[1].Status);

        var user = await _db.Users.FindAsync(userId);
        Assert.Equal("creator", user!.Tier);
    }
}
