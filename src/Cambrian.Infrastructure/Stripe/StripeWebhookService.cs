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
    private const string EventSubscriptionDeleted = "customer.subscription.deleted";
    private const string EventInvoicePaymentFailed = "invoice.payment_failed";
    private const string EventChargeRefunded = "charge.refunded";
    private const string EventChargeDisputeCreated = "charge.dispute.created";
    private const string StatusCompleted = "completed";
    private readonly CambrianDbContext _db;
    private readonly ILicenseService _licenseService;
    private readonly string _webhookSecret;
    private readonly ILogger<StripeWebhookService> _logger;

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
    }

    public async Task HandleStripeAsync(string payload, string signature)
    {
        string eventType;
        string? eventId;
        string? clientReferenceId;
        long? amountTotal;
        string? stripeSessionId = null;
        string? stripeCustomerId = null;
        string? stripePaymentIntentId = null;

        // ── Step 1: Verify signature — ALWAYS required ──
        if (string.IsNullOrEmpty(_webhookSecret))
        {
            _logger.LogError(
                "Stripe webhook rejected: Stripe:WebhookSecret is not configured. "
                + "Set the STRIPE_WEBHOOK_SECRET environment variable or Stripe:WebhookSecret in config. "
                + "For local development, use 'stripe listen --forward-to localhost:PORT/webhook/stripe' and set the signing secret it provides.");
            throw new InvalidOperationException(
                "Stripe webhook signature verification failed. "
                + "Stripe:WebhookSecret is not configured. Cannot process webhooks without signature verification.");
        }

        if (string.IsNullOrEmpty(signature))
        {
            _logger.LogError(
                "Stripe webhook rejected: Stripe-Signature header is missing. "
                + "Ensure requests are coming from Stripe (not a manual HTTP client without signing).");
            throw new InvalidOperationException(
                "Stripe webhook signature verification failed. "
                + "Stripe-Signature header is missing. All webhook requests must be signed.");
        }

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
        else if (eventType == EventSubscriptionDeleted)
        {
            var sub = stripeEvent.Data.Object as global::Stripe.Subscription;
            stripeCustomerId = sub?.CustomerId;
        }
        else if (eventType == EventInvoicePaymentFailed)
        {
            var invoice = stripeEvent.Data.Object as global::Stripe.Invoice;
            stripeCustomerId = invoice?.CustomerId;
        }
        else if (eventType == EventChargeRefunded)
        {
            var charge = stripeEvent.Data.Object as Charge;
            stripePaymentIntentId = charge?.PaymentIntentId;
        }
        else if (eventType == EventChargeDisputeCreated)
        {
            var dispute = stripeEvent.Data.Object as Dispute;
            stripePaymentIntentId = dispute?.Charge?.PaymentIntentId ?? dispute?.PaymentIntentId;
        }

        _logger.LogInformation("Stripe webhook verified: {EventType} {EventId}", eventType, eventId);
        await ProcessEventAsync(eventId, eventType, clientReferenceId, amountTotal, stripeCustomerId, stripeSessionId, stripePaymentIntentId, payload);
    }

    internal async Task ProcessEventAsync(
        string? eventId,
        string eventType,
        string? clientReferenceId,
        long? amountTotal,
        string? stripeCustomerId,
        string? stripeSessionId,
        string? stripePaymentIntentId = null,
        string? payload = null)
    {
        // ── Step 2: Idempotency — check for duplicate event ──
        if (!string.IsNullOrWhiteSpace(eventId))
        {
            var existing = await _db.StripeWebhookEvents
                .FirstOrDefaultAsync(e => e.EventId == eventId);

            if (existing is not null)
            {
                _logger.LogInformation("Skipping duplicate Stripe webhook event {EventId} (status={Status})", eventId, existing.Status);
                return;
            }
        }
        else
        {
            _logger.LogWarning(
                "Stripe webhook received without an event ID; idempotency unavailable for {EventType}",
                eventType);
        }

        // ── Step 3: Persist event as "received" BEFORE processing ──
        var webhookEvent = new StripeWebhookEvent
        {
            Id = Guid.NewGuid(),
            EventId = eventId ?? $"no-id-{Guid.NewGuid():N}",
            EventType = eventType,
            Payload = payload,
            Status = "received",
            Processed = false,
            ReceivedAt = DateTime.UtcNow,
            ProcessedAt = DateTime.UtcNow
        };
        _db.StripeWebhookEvents.Add(webhookEvent);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Stripe event persisted: {EventId} {EventType} status=received", webhookEvent.EventId, eventType);

        // ── Step 4: Process inside transaction, update status on success/failure ──
        webhookEvent.Status = "processing";
        await _db.SaveChangesAsync();

        var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;

        try
        {
            if (eventType == EventTypes.CheckoutSessionCompleted)
            {
                await HandleCheckoutCompleted(clientReferenceId, amountTotal, stripeSessionId);
            }
            else if (eventType == EventSubscriptionDeleted)
            {
                await HandleSubscriptionDeleted(stripeCustomerId);
            }
            else if (eventType == EventInvoicePaymentFailed)
            {
                await HandleInvoicePaymentFailed(stripeCustomerId);
            }
            else if (eventType == EventChargeRefunded)
            {
                await HandleChargeRefunded(stripePaymentIntentId);
            }
            else if (eventType == EventChargeDisputeCreated)
            {
                await HandleChargeDisputeCreated(stripePaymentIntentId);
            }

            webhookEvent.Status = "completed";
            webhookEvent.Processed = true;
            webhookEvent.ProcessedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            _logger.LogInformation("Stripe event completed: {EventId} {EventType}", webhookEvent.EventId, eventType);
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            // Mark event as failed (outside the rolled-back transaction)
            try
            {
                webhookEvent.Status = "failed";
                webhookEvent.ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                webhookEvent.Processed = false;
                await _db.SaveChangesAsync();
                _logger.LogError(ex, "Stripe event FAILED: {EventId} {EventType} — marked as failed for retry/investigation", webhookEvent.EventId, eventType);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Failed to update webhook event status to 'failed' for {EventId}", webhookEvent.EventId);
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
                purchase.Status = StatusCompleted;
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

        // ── Copyright buyout: atomic check-and-set to prevent race conditions ──
        if (licenseType == "copyright_buyout")
        {
            if (track.ExclusiveSold || track.Status == "copyright_transferred")
            {
                _logger.LogWarning("Track {TrackId} already sold/transferred — skipping copyright buyout for user {UserId}", trackId, userId);
                return;
            }

            var marked = await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Tracks\" SET \"ExclusiveSold\" = true, \"Status\" = 'copyright_transferred', \"Visibility\" = 'hidden', \"OriginalCreatorId\" = \"CreatorId\", \"CopyrightOwnerId\" = {userId}, \"CopyrightTransferredAt\" = {DateTime.UtcNow} WHERE \"Id\" = {trackId} AND \"ExclusiveSold\" = false AND \"Status\" != 'copyright_transferred'");
            if (marked == 0)
            {
                _logger.LogWarning("Copyright buyout race: Track {TrackId} was sold/transferred by another request — skipping for user {UserId}", trackId, userId);
                return;
            }
        }

        // Prevent duplicate purchases for the same track/user/license
        var existingPurchase = await _db.Purchases
            .FirstOrDefaultAsync(p => p.BuyerId == userId && p.TrackId == trackId && p.LicenseType == licenseType);

        if (existingPurchase is not null)
        {
            _logger.LogInformation("Duplicate purchase skipped for user {UserId} track {TrackId}", userId, trackId);
            if (existingPurchase.Status != StatusCompleted)
            {
                existingPurchase.Status = StatusCompleted;
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
            Status = StatusCompleted,
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

        // ── Issue invoice ──
        try
        {
            _db.Invoices.Add(new Cambrian.Domain.Entities.Invoice
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PurchaseId = purchase.Id,
                AmountCents = purchase.AmountCents,
                Currency = "usd",
                Status = "paid",
                IssuedAt = DateTime.UtcNow,
                PaidAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();
            _logger.LogInformation(
                "Invoice issued: PurchaseId={PurchaseId} UserId={UserId} AmountCents={AmountCents}",
                purchase.Id, userId, purchase.AmountCents);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create invoice for purchase {PurchaseId} — non-critical", purchase.Id);
        }

        // ── Append sale activity (display layer — never blocks purchase) ──
        try
        {
            var existingActivity = await _db.ActivityItems
                .AnyAsync(a => a.SourceId == purchase.Id && a.Type == "sale");
            if (!existingActivity)
            {
                _db.ActivityItems.Add(new ActivityItem
                {
                    Id = Guid.NewGuid(),
                    Type = "sale",
                    TrackId = purchase.TrackId,
                    UserId = purchase.BuyerId,
                    SourceId = purchase.Id,
                    IsSimulated = false,
                    CreatedAtUtc = purchase.CreatedAt
                });
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex2)
        {
            _logger.LogWarning(ex2, "Failed to create sale activity for purchase {PurchaseId} — non-critical", purchase.Id);
        }

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
            user.CreatorTier = string.Equals(tier, "pro", StringComparison.OrdinalIgnoreCase)
                ? CreatorTier.Pro
                : CreatorTier.Free;
            user.SubscriptionStatus = "Active";
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
        user.Tier = "free";
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
    /// Handle charge.refunded — revoke access for refunded purchases.
    /// Looks up the purchase by StripeSessionId (via PaymentIntent → Session).
    /// </summary>
    private async Task HandleChargeRefunded(string? stripePaymentIntentId)
    {
        if (string.IsNullOrEmpty(stripePaymentIntentId))
        {
            _logger.LogWarning("charge.refunded received without payment_intent ID");
            return;
        }

        // Look up the checkout session via the payment intent
        try
        {
            var sessionService = new SessionService();
            var sessions = await sessionService.ListAsync(new SessionListOptions
            {
                PaymentIntent = stripePaymentIntentId,
                Limit = 1
            });
            var session = sessions.FirstOrDefault();
            if (session is null)
            {
                _logger.LogWarning("charge.refunded: no checkout session found for PaymentIntent {PI}", stripePaymentIntentId);
                return;
            }

            var purchase = await _db.Purchases.FirstOrDefaultAsync(p => p.StripeSessionId == session.Id);
            if (purchase is null)
            {
                _logger.LogWarning("charge.refunded: no purchase found for session {SessionId}", session.Id);
                return;
            }

            purchase.Status = "refunded";
            purchase.UpdatedAt = DateTime.UtcNow;

            // Remove library access
            var libraryItem = await _db.Library
                .FirstOrDefaultAsync(l => l.UserId == purchase.BuyerId && l.TrackId == purchase.TrackId);
            if (libraryItem is not null)
                _db.Library.Remove(libraryItem);

            // SECURITY: Claw back creator wallet credit to prevent platform loss
            // Guard against double-clawback if webhook fires twice
            var alreadyClawedBack = await _db.WalletTransactions
                .AnyAsync(wt => wt.RelatedPurchaseId == purchase.Id && wt.Type == "debit"
                    && wt.Description != null && wt.Description.StartsWith("Refund clawback:"));
            if (!alreadyClawedBack)
            {
                var originalCredit = await _db.WalletTransactions
                    .FirstOrDefaultAsync(wt => wt.RelatedPurchaseId == purchase.Id && wt.Type == "credit");
                if (originalCredit is not null)
                {
                    _db.WalletTransactions.Add(new WalletTransaction
                    {
                        Id = Guid.NewGuid(),
                        UserId = originalCredit.UserId,
                        AmountCents = -originalCredit.AmountCents,
                        Type = "debit",
                        Description = $"Refund clawback: {purchase.Id}",
                        RelatedPurchaseId = purchase.Id,
                        CreatedAt = DateTime.UtcNow
                    });
                    _logger.LogInformation(
                        "Wallet clawback: debited {AmountCents}c from creator {CreatorId} for refunded purchase {PurchaseId}",
                        originalCredit.AmountCents, originalCredit.UserId, purchase.Id);
                }
            }

            await _db.SaveChangesAsync();
            _logger.LogInformation(
                "Refund processed: PurchaseId={PurchaseId} UserId={UserId} TrackId={TrackId}",
                purchase.Id, purchase.BuyerId, purchase.TrackId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process charge.refunded for PaymentIntent {PI}", stripePaymentIntentId);
        }
    }

    /// <summary>
    /// Handle charge.dispute.created — flag the purchase as disputed for review.
    /// </summary>
    private async Task HandleChargeDisputeCreated(string? stripePaymentIntentId)
    {
        if (string.IsNullOrEmpty(stripePaymentIntentId))
        {
            _logger.LogWarning("charge.dispute.created received without payment_intent ID");
            return;
        }

        try
        {
            var sessionService = new SessionService();
            var sessions = await sessionService.ListAsync(new SessionListOptions
            {
                PaymentIntent = stripePaymentIntentId,
                Limit = 1
            });
            var session = sessions.FirstOrDefault();
            if (session is null)
            {
                _logger.LogWarning("charge.dispute.created: no checkout session found for PaymentIntent {PI}", stripePaymentIntentId);
                return;
            }

            var purchase = await _db.Purchases.FirstOrDefaultAsync(p => p.StripeSessionId == session.Id);
            if (purchase is null)
            {
                _logger.LogWarning("charge.dispute.created: no purchase found for session {SessionId}", session.Id);
                return;
            }

            purchase.Status = "disputed";
            purchase.UpdatedAt = DateTime.UtcNow;

            // SECURITY: Claw back creator wallet credit on dispute to prevent platform loss
            // Guard against double-clawback if webhook fires twice
            var alreadyClawedBack = await _db.WalletTransactions
                .AnyAsync(wt => wt.RelatedPurchaseId == purchase.Id && wt.Type == "debit"
                    && wt.Description != null && wt.Description.StartsWith("Dispute clawback:"));
            if (!alreadyClawedBack)
            {
                var disputeCredit = await _db.WalletTransactions
                    .FirstOrDefaultAsync(wt => wt.RelatedPurchaseId == purchase.Id && wt.Type == "credit");
                if (disputeCredit is not null)
                {
                    _db.WalletTransactions.Add(new WalletTransaction
                    {
                        Id = Guid.NewGuid(),
                        UserId = disputeCredit.UserId,
                        AmountCents = -disputeCredit.AmountCents,
                        Type = "debit",
                        Description = $"Dispute clawback: {purchase.Id}",
                        RelatedPurchaseId = purchase.Id,
                        CreatedAt = DateTime.UtcNow
                    });
                    _logger.LogInformation(
                        "Wallet clawback: debited {AmountCents}c from creator {CreatorId} for disputed purchase {PurchaseId}",
                        disputeCredit.AmountCents, disputeCredit.UserId, purchase.Id);
                }
            }

            await _db.SaveChangesAsync();

            _logger.LogWarning(
                "Dispute opened: PurchaseId={PurchaseId} UserId={UserId} TrackId={TrackId} PaymentIntent={PI}",
                purchase.Id, purchase.BuyerId, purchase.TrackId, stripePaymentIntentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process charge.dispute.created for PaymentIntent {PI}", stripePaymentIntentId);
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