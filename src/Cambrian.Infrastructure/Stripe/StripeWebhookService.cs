using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
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
        _isDevelopment = env.IsDevelopment() || env.EnvironmentName == "Testing";
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

            await ProcessEventAsync(eventId, eventType, clientReferenceId, amountTotal, stripeCustomerId, stripeSessionId, payload);
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

        await ProcessEventAsync(eventId, eventType, clientReferenceId, amountTotal, stripeCustomerId, stripeSessionId, payload);
    }

    private async Task ProcessEventAsync(
        string? eventId,
        string eventType,
        string? clientReferenceId,
        long? amountTotal,
        string? stripeCustomerId,
        string? stripeSessionId,
        string? payload = null)
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
                Payload = payload,
                Processed = true,
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
            _logger.LogError("[DEAD-LETTER] Checkout session completed but no ClientReferenceId — paid session cannot be fulfilled. StripeSessionId={SessionId}", stripeSessionId);
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
                purchase.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                _logger.LogInformation("PURCHASE_TIMELINE: Purchase {PurchaseId} marked completed via legacy path", purchaseId);
            }
            else
            {
                _logger.LogError("[DEAD-LETTER] Legacy clientReferenceId GUID {PurchaseId} not found — paid session unfulfilled. StripeSessionId={SessionId}", purchaseId, stripeSessionId);
            }
        }
        else
        {
            _logger.LogError("[DEAD-LETTER] Unrecognized clientReferenceId format: {Ref} — paid session unfulfilled. StripeSessionId={SessionId}", clientReferenceId, stripeSessionId);
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
            _logger.LogError("[DEAD-LETTER] Invalid trackId in clientReferenceId: {TrackId} — paid session unfulfilled for user {UserId}. StripeSessionId={SessionId}", trackIdStr, userId, stripeSessionId);
            return;
        }

        var track = await _db.Tracks.FindAsync(trackId);
        if (track is null)
        {
            _logger.LogError("[DEAD-LETTER] Track {TrackId} not found — paid session unfulfilled for user {UserId}. StripeSessionId={SessionId}", trackId, userId, stripeSessionId);
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

        // ── Copyright buyout: mark track as transferred ──
        if (licenseType == "copyright_buyout")
        {
            if (track.ExclusiveSold || track.Status == "copyright_transferred")
            {
                _logger.LogWarning("Track {TrackId} already sold/transferred — skipping copyright buyout for user {UserId}", trackId, userId);
                return;
            }

            track.ExclusiveSold = true;
            track.Status = "copyright_transferred";
            track.Visibility = "hidden";
            track.OriginalCreatorId = track.CreatorId;
            track.CopyrightOwnerId = userId;
            track.CopyrightTransferredAt = DateTime.UtcNow;
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
                existingPurchase.UpdatedAt = DateTime.UtcNow;
                existingPurchase.StripeSessionId ??= stripeSessionId;
            }

            // Ensure library row exists even on duplicate purchase path
            var existingLibDup = await _db.Library
                .FirstOrDefaultAsync(l => l.UserId == userId && l.TrackId == trackId);
            if (existingLibDup is null)
            {
                _db.Library.Add(new LibraryItem
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    TrackId = trackId,
                    PurchaseId = existingPurchase.Id,
                    Title = track.Title,
                    Artist = "",
                    AudioUrl = track.AudioUrl,
                    SavedAt = DateTime.UtcNow
                });
                _logger.LogInformation("Library item back-filled for duplicate purchase: User={UserId} Track={TrackId}", userId, trackId);
            }
            else if (existingLibDup.PurchaseId is null)
            {
                existingLibDup.PurchaseId = existingPurchase.Id;
            }

            await _db.SaveChangesAsync();
            return;
        }

        // ── Create completed Purchase ──
        _logger.LogInformation("PURCHASE_TIMELINE: Creating purchase userId:{UserId} trackId:{TrackId} license:{LicenseType} amount:{AmountCents}c sessionId:{SessionId}",
            userId, trackId, licenseType, (int)(amountTotal ?? 0), stripeSessionId);
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
            UpdatedAt = DateTime.UtcNow,
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

        // ── Credit creator wallet using tier-based fee rate ──
        if (!string.IsNullOrEmpty(track.CreatorId) && amountTotal.HasValue && amountTotal.Value > 0)
        {
            var creatorUser = await _db.Users.FindAsync(track.CreatorId);
            var platformFeeRate = creatorUser is not null
                ? TierManifest.For(creatorUser.CreatorTier).FeeRate
                : TierManifest.Free.FeeRate;
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
                    "Credited creator {CreatorId} with {AmountCents} cents (fee={FeeRate}%) for track {TrackId}",
                    track.CreatorId, creatorCents, platformFeeRate * 100, trackId);
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
            _logger.LogError(ex,
                "[LICENSE-FAILED] Failed to issue license certificate for purchase {PurchaseId} — purchase and library created but license missing. Manual reconciliation required.",
                purchase.Id);
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "PURCHASE_TIMELINE: Purchase complete & library granted: User={UserId} Track={TrackId} License={License} PurchaseId={PurchaseId}",
            userId, trackId, licenseType, purchase.Id);

        // ── Library consistency check ──
        var libraryCheck = await _db.Library
            .AnyAsync(l => l.UserId == userId && l.TrackId == trackId);
        if (!libraryCheck)
        {
            _logger.LogError(
                "[CONSISTENCY] Purchase {PurchaseId} completed but NO library item found for user {UserId} track {TrackId}. Manual reconciliation needed.",
                purchase.Id, userId, trackId);
        }
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
    /// Matches the Stripe customer email to the local user account.
    /// </summary>
    private async Task HandleSubscriptionDeleted(string? stripeCustomerId)
    {
        if (string.IsNullOrEmpty(stripeCustomerId))
        {
            _logger.LogWarning("customer.subscription.deleted received without customer ID");
            return;
        }

        var user = await FindUserByStripeCustomerAsync(stripeCustomerId);
        if (user is null)
        {
            _logger.LogWarning(
                "customer.subscription.deleted: could not match Stripe customer {CustomerId} to a local user. Manual review needed.",
                stripeCustomerId);
            return;
        }

        var activeSub = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.UserId == user.Id && s.Status == "active");

        if (activeSub is not null)
        {
            activeSub.Status = "cancelled";
            activeSub.ExpiresAt = DateTime.UtcNow;
        }

        var wasPro = user.Tier is "pro" or "paid";
        var isCreator = user.Tier is "creator" or "pro";
        user.Tier = isCreator ? "creator" : "free";
        user.CreatorTier = CreatorTier.Free;
        user.SubscriptionStatus = "Cancelled";

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Subscription deleted via webhook: User={UserId} StripeCustomer={CustomerId} downgraded from {OldTier} to {NewTier}",
            user.Id, stripeCustomerId, wasPro ? "pro" : "paid", user.Tier);
    }

    /// <summary>
    /// Handle invoice.payment_failed — mark the user's subscription as at risk.
    /// Stripe will retry automatically; if all retries fail it sends customer.subscription.deleted.
    /// </summary>
    private async Task HandleInvoicePaymentFailed(string? stripeCustomerId)
    {
        if (string.IsNullOrEmpty(stripeCustomerId))
        {
            _logger.LogWarning("invoice.payment_failed received without customer ID");
            return;
        }

        var user = await FindUserByStripeCustomerAsync(stripeCustomerId);
        if (user is not null)
        {
            user.SubscriptionStatus = "PastDue";
            await _db.SaveChangesAsync();
            _logger.LogWarning(
                "Invoice payment failed: User={UserId} StripeCustomer={CustomerId} marked as PastDue",
                user.Id, stripeCustomerId);
        }
        else
        {
            _logger.LogWarning(
                "invoice.payment_failed: could not match Stripe customer {CustomerId} to a local user.",
                stripeCustomerId);
        }
    }

    /// <summary>
    /// Attempt to match a Stripe customer ID to a local ApplicationUser by looking up
    /// the customer's email in Stripe and matching it to our users table.
    /// </summary>
    private async Task<ApplicationUser?> FindUserByStripeCustomerAsync(string stripeCustomerId)
    {
        try
        {
            var customerService = new CustomerService();
            var customer = await customerService.GetAsync(stripeCustomerId);
            if (!string.IsNullOrEmpty(customer?.Email))
            {
                return await _db.Users
                    .FirstOrDefaultAsync(u => u.NormalizedEmail == customer.Email.ToUpperInvariant());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to look up Stripe customer {CustomerId}", stripeCustomerId);
        }

        return null;
    }
}