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
    private readonly ILicenseService _licenseService;
    private readonly string _webhookSecret;
    private readonly ILogger<StripeWebhookService> _logger;
    private readonly bool _isDevelopment;

    public StripeWebhookService(
        CambrianDbContext db,
        ILicenseService licenseService,
        IConfiguration configuration,
        ILogger<StripeWebhookService> logger,
        IHostEnvironment env)
    {
        _db = db;
        _licenseService = licenseService;
        _webhookSecret = configuration["Stripe:WebhookSecret"] ?? "";
        _logger = logger;
        _isDevelopment = env.IsDevelopment();
    }

    public async Task HandleStripeAsync(string payload, string signature)
    {
        string eventType;
        string? eventId;
        string? clientReferenceId;
        long? amountTotal;
        string? stripeSessionId = null;
        string? stripeSubscriptionId = null;
        string? stripeCustomerId = null;

        // ── Verified path: both secret and signature present ──
        if (!string.IsNullOrEmpty(_webhookSecret) && !string.IsNullOrEmpty(signature))
        {
            var stripeEvent = EventUtility.ConstructEvent(payload, signature, _webhookSecret);
            eventType = stripeEvent.Type;
            eventId = stripeEvent.Id;
            clientReferenceId = null;
            amountTotal = null;

            if (eventType == EventTypes.CheckoutSessionCompleted)
            {
                var session = stripeEvent.Data.Object as Session;
                clientReferenceId = session?.ClientReferenceId;
                amountTotal = session?.AmountTotal;
                stripeSessionId = session?.Id;
            }
            else if (eventType == "customer.subscription.deleted")
            {
                var sub = stripeEvent.Data.Object as global::Stripe.Subscription;
                stripeSubscriptionId = sub?.Id;
                stripeCustomerId = sub?.CustomerId;
            }
            else if (eventType == "invoice.payment_failed")
            {
                var invoice = stripeEvent.Data.Object as global::Stripe.Invoice;
                stripeSubscriptionId = invoice?.SubscriptionId;
                stripeCustomerId = invoice?.CustomerId;
            }

            await ProcessEventAsync(eventId, eventType, clientReferenceId, amountTotal, stripeCustomerId, stripeSessionId);
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
        eventId = root.TryGetProperty("id", out var eventIdElement) ? eventIdElement.GetString() : null;
        clientReferenceId = null;
        amountTotal = null;
        _logger.LogInformation("Stripe webhook received (dev): {EventType}", eventType);

        if (eventType == "checkout.session.completed")
        {
            var dataObj = root.GetProperty("data").GetProperty("object");
            clientReferenceId = dataObj.TryGetProperty("client_reference_id", out var cri) ? cri.GetString() : null;
            amountTotal = dataObj.TryGetProperty("amount_total", out var at) ? at.GetInt64() : null;
            stripeSessionId = dataObj.TryGetProperty("id", out var sid) ? sid.GetString() : null;
        }
        else if (eventType is "customer.subscription.deleted" or "invoice.payment_failed")
        {
            var dataObj = root.GetProperty("data").GetProperty("object");
            stripeCustomerId = dataObj.TryGetProperty("customer", out var cust) ? cust.GetString() : null;
        }

        await ProcessEventAsync(eventId, eventType, clientReferenceId, amountTotal, stripeCustomerId, stripeSessionId);
    }

    private async Task ProcessEventAsync(
        string? eventId,
        string eventType,
        string? clientReferenceId,
        long? amountTotal,
        string? stripeCustomerId,
        string? stripeSessionId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            _logger.LogWarning(
                "Stripe webhook received without an event ID; idempotency unavailable for {EventType}",
                eventType);

            if (eventType == EventTypes.CheckoutSessionCompleted)
            {
                await HandleCheckoutCompleted(clientReferenceId, amountTotal, stripeSessionId);
            }
            else if (eventType == "customer.subscription.deleted")
            {
                await HandleSubscriptionDeleted(stripeCustomerId);
            }
            else if (eventType == "invoice.payment_failed")
            {
                await HandleInvoicePaymentFailed(stripeCustomerId);
            }

            return;
        }

        var alreadyProcessed = await _db.StripeWebhookEvents
            .AsNoTracking()
            .AnyAsync(e => e.EventId == eventId);

        if (alreadyProcessed)
        {
            _logger.LogInformation("Skipping duplicate Stripe webhook event {EventId}", eventId);
            return;
        }

        var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;

        try
        {
            _db.StripeWebhookEvents.Add(new StripeWebhookEvent
            {
                Id = Guid.NewGuid(),
                EventId = eventId,
                EventType = eventType,
                ProcessedAt = DateTime.UtcNow
            });

            if (eventType == EventTypes.CheckoutSessionCompleted)
            {
                await HandleCheckoutCompleted(clientReferenceId, amountTotal, stripeSessionId);
            }
            else if (eventType == "customer.subscription.deleted")
            {
                await HandleSubscriptionDeleted(stripeCustomerId);
            }
            else if (eventType == "invoice.payment_failed")
            {
                await HandleInvoicePaymentFailed(stripeCustomerId);
            }

            await _db.SaveChangesAsync();

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
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
        // CheckoutService sets clientReferenceId = "userId:trackId:licenseType[:usageType]"
        var parts = clientReferenceId.Split(':');
        if (parts.Length >= 3 && parts[1] == "subscription")
        {
            await HandleSubscriptionCheckout(parts[0], parts[2]);
            return;
        }

        if (parts.Length >= 3)
        {
            var usageType = parts.Length >= 4 ? parts[3] : "personal";
            await HandleTrackPurchase(parts[0], parts[1], parts[2], usageType, amountTotal, stripeSessionId);
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
    /// Handle a track purchase: create Purchase record, issue license, add to Library.
    /// </summary>
    private async Task HandleTrackPurchase(
        string userId, string trackIdStr, string licenseType,
        string usageType, long? amountTotal, string? stripeSessionId)
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

        // ── Exclusive: atomic check-and-set to prevent race conditions ──
        if (licenseType == "exclusive")
        {
            if (track.ExclusiveSold)
            {
                _logger.LogWarning("Track {TrackId} already sold exclusively — skipping purchase for user {UserId}", trackId, userId);
                return;
            }

            var marked = await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Tracks\" SET \"ExclusiveSold\" = true WHERE \"Id\" = {trackId} AND \"ExclusiveSold\" = false");
            if (marked == 0)
            {
                _logger.LogWarning("Exclusive race: Track {TrackId} was sold by another request — skipping for user {UserId}", trackId, userId);
                return;
            }
        }

        // Prevent duplicate purchases for the same track/user/license
        var existingPurchase = await _db.Purchases
            .FirstOrDefaultAsync(p => p.BuyerId == userId && p.TrackId == trackId && p.LicenseType == licenseType);

        if (existingPurchase is not null)
        {
            _logger.LogInformation("Duplicate purchase skipped for user {UserId} track {TrackId}", userId, trackId);
            if (existingPurchase.Status != "completed")
            {
                existingPurchase.Status = "completed";
                existingPurchase.CompletedAt = DateTime.UtcNow;
                existingPurchase.StripeSessionId ??= stripeSessionId;
            }
            await _db.SaveChangesAsync();
            return;
        }

        // ── Create completed Purchase ──
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = userId,
            TrackId = trackId,
            AmountCents = (int)(amountTotal ?? 0),
            PaymentMethod = "stripe",
            LicenseType = licenseType,
            UsageType = usageType,
            Status = "completed",
            StripeSessionId = stripeSessionId,
            CompletedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        _db.Purchases.Add(purchase);

        // ── Add to library (if not already there), with PurchaseId FK ──
        var existingLib = await _db.Library
            .FirstOrDefaultAsync(l => l.UserId == userId && l.TrackId == trackId);

        if (existingLib is null)
        {
            var libraryItem = new LibraryItem
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TrackId = trackId,
                PurchaseId = purchase.Id,
                Title = track.Title,
                Artist = "",
                AudioUrl = track.AudioUrl,
                SavedAt = DateTime.UtcNow
            };
            _db.Library.Add(libraryItem);
        }
        else if (existingLib.PurchaseId is null)
        {
            existingLib.PurchaseId = purchase.Id;
        }

        // ── Credit creator wallet (platform takes 15% fee) ──
        if (!string.IsNullOrEmpty(track.CreatorId) && amountTotal.HasValue && amountTotal.Value > 0)
        {
            const decimal platformFeeRate = 0.15m;
            var grossCents = amountTotal.Value;
            var creatorCents = (long)Math.Floor(grossCents * (1 - platformFeeRate));

            if (creatorCents > 0)
            {
                _db.WalletTransactions.Add(new WalletTransaction
                {
                    Id = Guid.NewGuid(),
                    UserId = track.CreatorId,
                    AmountCents = creatorCents,
                    Type = "credit",
                    Description = $"Sale: {track.Title} ({licenseType})",
                    RelatedPurchaseId = purchase.Id,
                    CreatedAt = DateTime.UtcNow
                });
                _logger.LogInformation(
                    "Credited creator {CreatorId} with {AmountCents} cents for track {TrackId}",
                    track.CreatorId, creatorCents, trackId);
            }
        }

        // ── Issue license certificate ──
        try
        {
            var cert = await _licenseService.IssueCertificateAsync(
                purchase.Id,
                track.CambrianTrackId ?? trackIdStr,
                userId,
                track.CreatorId,
                licenseType,
                usageType);
            purchase.LicenseId = Guid.TryParse(cert.LicenseId, out var licId) ? licId : null;
            _logger.LogInformation(
                "License certificate issued: PurchaseId={PurchaseId} LicenseId={LicenseId}",
                purchase.Id, cert.LicenseId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to issue license certificate for webhook purchase {PurchaseId}", purchase.Id);
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Purchase created & track added to library: User={UserId} Track={TrackId} License={License}",
            userId, trackId, licenseType);
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

    /// <summary>
    /// Handle customer.subscription.deleted — downgrade user to free tier.
    /// Stripe sends this when a subscription is cancelled (end of billing period or immediate).
    /// NOTE: We cannot reliably match Stripe customer ID to our user yet.
    /// A future improvement is to store StripeCustomerId on ApplicationUser during checkout.
    /// For now, this logs the event so it can be handled manually if needed.
    /// </summary>
    private async Task HandleSubscriptionDeleted(string? stripeCustomerId)
    {
        if (string.IsNullOrEmpty(stripeCustomerId))
        {
            _logger.LogWarning("customer.subscription.deleted received without customer ID");
            return;
        }

        _logger.LogWarning(
            "Stripe subscription deleted for customer {CustomerId}. " +
            "Manual review may be needed to downgrade the user's tier.",
            stripeCustomerId);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Handle invoice.payment_failed — log the failure.
    /// Stripe will retry payment automatically according to its retry schedule.
    /// If all retries fail, Stripe sends customer.subscription.deleted.
    /// </summary>
    private async Task HandleInvoicePaymentFailed(string? stripeCustomerId)
    {
        _logger.LogWarning(
            "Invoice payment failed for Stripe customer {CustomerId}. " +
            "Stripe will retry or cancel the subscription automatically.",
            stripeCustomerId ?? "unknown");

        await Task.CompletedTask;
    }
}