using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;
using System.Text.Json;

namespace Cambrian.Infrastructure.Stripe;

public class StripeWebhookService : IWebhookService
{
    private readonly CambrianDbContext _db;
    private readonly string _webhookSecret;
    private readonly ILogger<StripeWebhookService> _logger;
    private readonly bool _isDevelopment;

    public StripeWebhookService(
        CambrianDbContext db,
        IConfiguration configuration,
        ILogger<StripeWebhookService> logger,
        IHostEnvironment env)
    {
        _db = db;
        _webhookSecret = configuration["Stripe:WebhookSecret"] ?? "";
        _logger = logger;
        _isDevelopment = env.IsDevelopment();
    }

    public async Task HandleStripeAsync(string payload, string signature)
    {
        string eventType;
        string? eventKey;
        string? stripeSessionId = null;
        string? clientReferenceId;
        long? amountTotal;

        // ── Verified path: both secret and signature present ──
        if (!string.IsNullOrEmpty(_webhookSecret) && !string.IsNullOrEmpty(signature))
        {
            var stripeEvent = EventUtility.ConstructEvent(payload, signature, _webhookSecret);
            eventType = stripeEvent.Type;

            if (eventType == EventTypes.CheckoutSessionCompleted)
            {
                var session = stripeEvent.Data.Object as Session;
                stripeSessionId = session?.Id;
                clientReferenceId = session?.ClientReferenceId;
                amountTotal = session?.AmountTotal;
            }
            else
            {
                clientReferenceId = null;
                amountTotal = null;
            }

            eventKey = BuildEventKey(stripeEvent.Id, eventType, stripeSessionId);
            if (await HasProcessedEventAsync(eventKey))
                return;

            if (eventType == EventTypes.CheckoutSessionCompleted)
                await HandleCheckoutCompleted(clientReferenceId, amountTotal, stripeSessionId);

            await RecordProcessedEventAsync(eventKey, eventType);

            _logger.LogInformation("Stripe webhook received (verified): {EventType}", eventType);
            return;
        }

        // ── Non-Development: REJECT unverified webhooks ──
        if (!_isDevelopment)
        {
            _logger.LogError(
                "Stripe webhook rejected: signature verification failed. "
                + "WebhookSecret configured={SecretPresent}, Stripe-Signature header present={SigPresent}",
                !string.IsNullOrEmpty(_webhookSecret),
                !string.IsNullOrEmpty(signature));
            throw new InvalidOperationException(
                "Stripe webhook signature verification failed. "
                + "Ensure Stripe:WebhookSecret is configured and the request includes a valid Stripe-Signature header.");
        }

        // ── Development-only fallback: parse JSON without signature ──
        _logger.LogWarning("Processing webhook WITHOUT signature verification (Development only)");
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        eventType = root.GetProperty("type").GetString() ?? "";
        _logger.LogInformation("Stripe webhook received (dev): {EventType}", eventType);
        eventKey = root.TryGetProperty("id", out var eventIdProp) ? eventIdProp.GetString() : null;

        if (eventType == "checkout.session.completed")
        {
            var dataObj = root.GetProperty("data").GetProperty("object");
            stripeSessionId = dataObj.TryGetProperty("id", out var sid) ? sid.GetString() : null;
            clientReferenceId = dataObj.TryGetProperty("client_reference_id", out var cri) ? cri.GetString() : null;
            amountTotal = dataObj.TryGetProperty("amount_total", out var at) ? at.GetInt64() : null;
            eventKey = BuildEventKey(eventKey, eventType, stripeSessionId);

            if (await HasProcessedEventAsync(eventKey))
                return;

            await HandleCheckoutCompleted(clientReferenceId, amountTotal, stripeSessionId);
            await RecordProcessedEventAsync(eventKey, eventType);
        }
        else if (!string.IsNullOrWhiteSpace(eventKey))
        {
            if (await HasProcessedEventAsync(eventKey))
                return;

            await RecordProcessedEventAsync(eventKey, eventType);
        }
    }

    private async Task HandleCheckoutCompleted(string? clientReferenceId, long? amountTotal, string? stripeSessionId)
    {
        if (clientReferenceId is null)
        {
            _logger.LogWarning("Checkout session completed but no ClientReferenceId");
            return;
        }

        // BillingController sets clientReferenceId = "userId:subscription:tier"
        // CheckoutService sets clientReferenceId = "userId:trackId:licenseType"
        var parts = clientReferenceId.Split(':');
        if (parts.Length == 3 && parts[1] == "subscription")
        {
            await HandleSubscriptionCheckout(parts[0], parts[2]);
            return;
        }

        if (parts.Length == 3)
        {
            await HandleTrackPurchase(parts[0], parts[1], parts[2], amountTotal, stripeSessionId);
            return;
        }

        // Fallback: try parsing as a purchase GUID (legacy/PaymentService path)
        if (Guid.TryParse(clientReferenceId, out var purchaseId))
        {
            var purchase = await _db.Purchases.FindAsync(purchaseId);
            if (purchase is not null)
            {
                purchase.Status = "completed";
                await _db.SaveChangesAsync();
                _logger.LogInformation("Purchase {PurchaseId} marked completed", purchaseId);
            }
        }
    }

    /// <summary>
    /// Handle a track purchase: create Purchase record + add to Library.
    /// </summary>
    private async Task HandleTrackPurchase(
        string userId,
        string trackIdStr,
        string licenseType,
        long? amountTotal,
        string? stripeSessionId)
    {
        if (!Guid.TryParse(trackIdStr, out var trackId))
        {
            _logger.LogWarning("Invalid trackId in clientReferenceId: {TrackId}", trackIdStr);
            return;
        }

        var track = await _db.Tracks.FindAsync(trackId);
        if (track is null)
        {
            _logger.LogWarning("Track {TrackId} not found for webhook", trackId);
            return;
        }

        var normalizedLicenseType = string.IsNullOrWhiteSpace(licenseType)
            ? "non-exclusive"
            : licenseType.Trim().ToLowerInvariant();
        var expectedAmountCents = normalizedLicenseType == "exclusive"
            ? (track.ExclusivePriceCents > 0 ? track.ExclusivePriceCents : (int)Math.Round(track.Price * 100, MidpointRounding.AwayFromZero))
            : (track.NonExclusivePriceCents > 0 ? track.NonExclusivePriceCents : (int)Math.Round(track.Price * 100, MidpointRounding.AwayFromZero));
        var finalAmountCents = amountTotal.HasValue && amountTotal.Value > 0
            ? (int)amountTotal.Value
            : expectedAmountCents;

        // Prevent duplicate purchases for the same track/user/license
        var existingPurchase = await _db.Purchases
            .FirstOrDefaultAsync(p => p.BuyerId == userId && p.TrackId == trackId && p.LicenseType == normalizedLicenseType);

        if (normalizedLicenseType == "exclusive" && track.ExclusiveSold && existingPurchase is null)
        {
            _logger.LogWarning("Exclusive track {TrackId} already sold; webhook ignored", trackId);
            return;
        }

        Purchase purchase;

        if (existingPurchase is not null)
        {
            _logger.LogInformation("Duplicate purchase skipped for user {UserId} track {TrackId}", userId, trackId);
            existingPurchase.Status = "completed";
            existingPurchase.Amount = finalAmountCents / 100.0;
            existingPurchase.PaymentMethod = "stripe";
            existingPurchase.StripeSessionId = stripeSessionId ?? existingPurchase.StripeSessionId;
            purchase = existingPurchase;
        }
        else
        {
            // Create completed Purchase
            purchase = new Purchase
            {
                Id = Guid.NewGuid(),
                BuyerId = userId,
                TrackId = trackId,
                Amount = finalAmountCents / 100.0,
                PaymentMethod = "stripe",
                StripeSessionId = stripeSessionId,
                LicenseType = normalizedLicenseType,
                Status = "completed",
                CreatedAt = DateTime.UtcNow
            };
            _db.Purchases.Add(purchase);
        }

        // Add to library (if not already there)
        var existingLib = await _db.Library
            .FirstOrDefaultAsync(l => l.UserId == userId && l.TrackId == trackId);

        if (existingLib is null)
        {
            var libraryItem = new LibraryItem
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TrackId = trackId,
                Title = track.Title,
                Artist = "",
                AudioUrl = track.AudioUrl,
                SavedAt = DateTime.UtcNow
            };
            _db.Library.Add(libraryItem);
        }

        if (normalizedLicenseType == "exclusive" && !track.ExclusiveSold)
            track.ExclusiveSold = true;

        var invoiceExists = await _db.Invoices.AnyAsync(i => i.PurchaseId == purchase.Id);
        if (!invoiceExists)
        {
            _db.Invoices.Add(new Cambrian.Domain.Entities.Invoice
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PurchaseId = purchase.Id,
                AmountCents = finalAmountCents,
                Currency = "usd",
                Status = "paid",
                IssuedAt = DateTime.UtcNow,
                PaidAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Purchase created & track added to library: User={UserId} Track={TrackId} License={License}",
            userId, trackId, normalizedLicenseType);
    }

    /// <summary>
    /// Handle a subscription checkout: create or update the user's Subscription record
    /// and upgrade their tier on the ApplicationUser.
    /// </summary>
    private async Task HandleSubscriptionCheckout(string userId, string tier)
    {
        // Cancel any existing subscription for this user
        var existing = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Status == "active");

        if (existing is not null)
        {
            existing.Status = "cancelled";
            existing.ExpiresAt = DateTime.UtcNow;
        }

        // Create new subscription
        var subscription = new Cambrian.Domain.Entities.Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Plan = tier,
            Status = "active",
            StartedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMonths(1)
        };
        _db.Subscriptions.Add(subscription);

        // Update user tier
        var user = await _db.Users.FindAsync(userId);
        if (user is not null)
        {
            user.Tier = tier;
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Subscription activated: User={UserId} Plan={Plan}",
            userId, tier);
    }

    private static string? BuildEventKey(string? eventId, string eventType, string? stripeSessionId)
    {
        if (!string.IsNullOrWhiteSpace(eventId))
            return $"evt:{eventId}";

        if (eventType == EventTypes.CheckoutSessionCompleted && !string.IsNullOrWhiteSpace(stripeSessionId))
            return $"checkout:{stripeSessionId}";

        return null;
    }

    private async Task<bool> HasProcessedEventAsync(string? eventKey)
    {
        if (string.IsNullOrWhiteSpace(eventKey))
            return false;

        var alreadyProcessed = await _db.StripeWebhookEvents.AnyAsync(e => e.EventId == eventKey);
        if (alreadyProcessed)
            _logger.LogInformation("Duplicate Stripe webhook ignored: {EventKey}", eventKey);

        return alreadyProcessed;
    }

    private async Task RecordProcessedEventAsync(string? eventKey, string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventKey))
            return;

        _db.StripeWebhookEvents.Add(new StripeWebhookEvent
        {
            EventId = eventKey,
            EventType = eventType,
            ProcessedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();
    }
}