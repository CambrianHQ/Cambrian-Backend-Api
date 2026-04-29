using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Pricing;
using Cambrian.Domain.Constants;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cambrian.Infrastructure.Stripe;

public class StripeWebhookService : IWebhookService
{
    private const string EventSubscriptionDeleted = "customer.subscription.deleted";
    private const string EventInvoicePaid = "invoice.paid";
    private const string EventInvoicePaymentFailed = "invoice.payment_failed";
    private const string EventPaymentIntentSucceeded = "payment_intent.succeeded";
    private const string EventChargeRefunded = "charge.refunded";
    private const string EventChargeDisputeCreated = "charge.dispute.created";
    private readonly CambrianDbContext _db;
    private readonly ILicenseService _licenseService;
    private readonly IEmailService _email;
    private readonly IConfiguration _config;
    private readonly string _webhookSecret;
    private readonly ILogger<StripeWebhookService> _logger;

    public StripeWebhookService(
        CambrianDbContext db,
        ILicenseService licenseService,
        IEmailService email,
        IConfiguration configuration,
        ILogger<StripeWebhookService> logger,
        IHostEnvironment env)
    {
        _db = db;
        _licenseService = licenseService;
        _email = email;
        _config = configuration;
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

        try
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
                stripeCustomerId = session?.CustomerId;
            }
            else if (eventType == EventSubscriptionDeleted)
            {
                var sub = stripeEvent.Data.Object as global::Stripe.Subscription;
                stripeCustomerId = sub?.CustomerId;
            }
            else if (eventType == EventInvoicePaid)
            {
                var invoice = stripeEvent.Data.Object as global::Stripe.Invoice;
                stripeCustomerId = invoice?.CustomerId;
            }
            else if (eventType == EventInvoicePaymentFailed)
            {
                var invoice = stripeEvent.Data.Object as global::Stripe.Invoice;
                stripeCustomerId = invoice?.CustomerId;
            }
            else if (eventType == EventPaymentIntentSucceeded)
            {
                var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
                stripePaymentIntentId = paymentIntent?.Id;
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
        }
        catch (NullReferenceException ex)
        {
            _logger.LogWarning(ex,
                "Stripe webhook event deserialization failed after signature verification. Falling back to minimal JSON parsing.");

            ValidateStripeSignature(payload, signature, _webhookSecret);
            (eventId, eventType, clientReferenceId, amountTotal, stripeCustomerId, stripeSessionId, stripePaymentIntentId)
                = ParseSignedEventFallback(payload);
        }

        _logger.LogInformation("Stripe webhook verified: {EventType} {EventId}", eventType, eventId);
        await ProcessEventAsync(eventId, eventType, clientReferenceId, amountTotal, stripeCustomerId, stripeSessionId, stripePaymentIntentId, payload);
    }

    private static void ValidateStripeSignature(string payload, string signatureHeader, string secret)
    {
        var timestamp = 0L;
        var signatures = new List<string>();

        foreach (var segment in signatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = segment.Split('=', 2);
            if (kv.Length != 2)
                continue;

            if (string.Equals(kv[0], "t", StringComparison.OrdinalIgnoreCase) && long.TryParse(kv[1], out var parsed))
                timestamp = parsed;
            else if (string.Equals(kv[0], "v1", StringComparison.OrdinalIgnoreCase))
                signatures.Add(kv[1]);
        }

        if (timestamp <= 0 || signatures.Count == 0)
            throw new InvalidOperationException("Stripe webhook signature verification failed. Signature header is malformed.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp) > 300)
            throw new InvalidOperationException("Stripe webhook signature verification failed. Signature timestamp is outside the allowed tolerance.");

        var signedPayload = $"{timestamp}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload))).ToLowerInvariant();
        var expectedBytes = Encoding.ASCII.GetBytes(expected);

        foreach (var candidate in signatures)
        {
            var candidateBytes = Encoding.ASCII.GetBytes(candidate);
            if (candidateBytes.Length == expectedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(candidateBytes, expectedBytes))
            {
                return;
            }
        }

        throw new InvalidOperationException("Stripe webhook signature verification failed. No matching v1 signature was found.");
    }

    private static (string? EventId, string EventType, string? ClientReferenceId, long? AmountTotal,
        string? StripeCustomerId, string? StripeSessionId, string? StripePaymentIntentId)
        ParseSignedEventFallback(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var eventType = root.GetProperty("type").GetString()
            ?? throw new JsonException("Webhook payload is missing type.");
        var eventId = root.TryGetProperty("id", out var eventIdProp) ? eventIdProp.GetString() : null;

        string? clientReferenceId = null;
        long? amountTotal = null;
        string? stripeCustomerId = null;
        string? stripeSessionId = null;
        string? stripePaymentIntentId = null;

        if (root.TryGetProperty("data", out var data) && data.TryGetProperty("object", out var obj))
        {
            if (eventType == EventTypes.CheckoutSessionCompleted)
            {
                clientReferenceId = obj.TryGetProperty("client_reference_id", out var refProp) ? refProp.GetString() : null;
                amountTotal = obj.TryGetProperty("amount_total", out var amountProp) && amountProp.ValueKind == JsonValueKind.Number
                    ? amountProp.GetInt64()
                    : null;
                stripeSessionId = obj.TryGetProperty("id", out var sessionProp) ? sessionProp.GetString() : null;
                stripeCustomerId = obj.TryGetProperty("customer", out var customerProp) ? customerProp.GetString() : null;
            }
            else if (eventType is EventSubscriptionDeleted or EventInvoicePaid or EventInvoicePaymentFailed)
            {
                stripeCustomerId = obj.TryGetProperty("customer", out var customerProp) ? customerProp.GetString() : null;
            }
            else if (eventType is EventPaymentIntentSucceeded or EventChargeRefunded or EventChargeDisputeCreated)
            {
                stripePaymentIntentId = obj.TryGetProperty("payment_intent", out var piProp) ? piProp.GetString() : null;
                stripePaymentIntentId ??= obj.TryGetProperty("id", out var intentProp) ? intentProp.GetString() : null;
            }
        }

        return (eventId, eventType, clientReferenceId, amountTotal, stripeCustomerId, stripeSessionId, stripePaymentIntentId);
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
        // ── Step 2: Idempotency — REQUIRE an event ID. ──
        // Without an EventId we cannot dedupe; processing the same event twice would
        // create duplicate Purchases and double-credit creator wallets. Reject so Stripe
        // retries until a legitimate signed event with an ID arrives. (Signature
        // verification has already passed at this point, so this only blocks malformed
        // or hand-crafted traffic.)
        if (string.IsNullOrWhiteSpace(eventId))
        {
            _logger.LogError(
                "Stripe webhook rejected: signed event missing EventId for {EventType}. " +
                "Idempotency requires an EventId — refusing to process to avoid double-fulfillment.",
                eventType);
            throw new InvalidOperationException(
                "Stripe webhook rejected: event has no EventId; cannot guarantee idempotency.");
        }

        var normalizedEventId = eventId;

        var alreadyCompleted = await _db.StripeWebhookEvents
            .AnyAsync(e => e.EventId == eventId && e.Status == "completed");

        if (alreadyCompleted)
        {
            _logger.LogInformation("Skipping already-completed Stripe webhook event {EventId}", eventId);
            return;
        }

        // ── Step 3: Begin transaction FIRST — event row and all business effects commit atomically ──
        var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;

        // Upsert: if a previous attempt left a "failed" row, update it; otherwise insert fresh.
        var existingRow = await _db.StripeWebhookEvents
            .FirstOrDefaultAsync(e => e.EventId == normalizedEventId);

        StripeWebhookEvent webhookEvent;
        if (existingRow is not null)
        {
            webhookEvent = existingRow;
            webhookEvent.Status = "processing";
            webhookEvent.ErrorMessage = null;
            webhookEvent.Processed = false;
            webhookEvent.ProcessedAt = DateTime.UtcNow;
            _logger.LogInformation("Retrying previously failed Stripe webhook event {EventId}", normalizedEventId);
        }
        else
        {
            webhookEvent = new StripeWebhookEvent
            {
                Id = Guid.NewGuid(),
                EventId = normalizedEventId,
                EventType = eventType,
                Payload = payload,
                Status = "processing",
                Processed = false,
                ReceivedAt = DateTime.UtcNow,
                ProcessedAt = DateTime.UtcNow
            };
            _db.StripeWebhookEvents.Add(webhookEvent);
        }

        _logger.LogInformation("Stripe event processing: {EventId} {EventType}", normalizedEventId, eventType);

        try
        {
            // ── Step 4: Process inside transaction ──
            if (eventType == EventTypes.CheckoutSessionCompleted)
            {
                await HandleCheckoutCompleted(clientReferenceId, amountTotal, stripeSessionId, stripeCustomerId);
            }
            else if (eventType == EventSubscriptionDeleted)
            {
                await HandleSubscriptionDeleted(stripeCustomerId);
            }
            else if (eventType == EventInvoicePaid)
            {
                await HandleInvoicePaid(stripeCustomerId);
            }
            else if (eventType == EventInvoicePaymentFailed)
            {
                await HandleInvoicePaymentFailed(stripeCustomerId);
            }
            else if (eventType == EventPaymentIntentSucceeded)
            {
                await HandlePaymentIntentSucceeded(stripePaymentIntentId);
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

            // Single SaveChanges commits event row + all business side effects together.
            await _db.SaveChangesAsync();

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            _logger.LogInformation("Stripe event completed: {EventId} {EventType}", normalizedEventId, eventType);
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            // After rollback the change tracker contains stale/inconsistent state.
            // Clear it so the failed-marker save starts from a clean baseline.
            _db.ChangeTracker.Clear();

            // Record failure for observability — this is a best-effort, non-transactional save.
            try
            {
                // After rollback the event row may or may not exist in DB depending on
                // whether it was a new insert (rolled back) or an update of an existing row.
                var failedRecord = await _db.StripeWebhookEvents
                    .FirstOrDefaultAsync(e => e.EventId == normalizedEventId);

                if (failedRecord is null)
                {
                    failedRecord = new StripeWebhookEvent
                    {
                        Id = Guid.NewGuid(),
                        EventId = normalizedEventId,
                        EventType = eventType,
                        Payload = payload,
                        ReceivedAt = DateTime.UtcNow
                    };
                    _db.StripeWebhookEvents.Add(failedRecord);
                }

                failedRecord.Status = "failed";
                failedRecord.Processed = false;
                failedRecord.ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                failedRecord.ProcessedAt = DateTime.UtcNow;

                await _db.SaveChangesAsync();
                _logger.LogError(ex, "Stripe event FAILED: {EventId} {EventType} — marked as failed for retry/investigation", normalizedEventId, eventType);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Failed to record webhook failure for {EventId}", normalizedEventId);
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

    private async Task HandleCheckoutCompleted(string? clientReferenceId, long? amountTotal, string? stripeSessionId, string? stripeCustomerId)
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
            await HandleSubscriptionCheckout(parts[0], parts[2], stripeCustomerId);
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
                purchase.Status = PurchaseStatuses.Completed;
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

        // H4: Price re-validation — guard against checkout price manipulation.
        // Compare the Stripe-reported amount against the track's current listed price.
        // A legitimate checkout is always server-created, so a charge below 50% of the
        // current price is anomalous and is refunded rather than fulfilled.
        var expectedCents = licenseType switch
        {
            "exclusive"        => track.ExclusivePriceCents,
            "nonexclusive"     => track.NonExclusivePriceCents,
            "copyright_buyout" => track.CopyrightBuyoutPriceCents,
            _                  => track.NonExclusivePriceCents,
        };
        if (expectedCents > 0 && amountTotal.HasValue)
        {
            if (amountTotal.Value < expectedCents / 2)
            {
                _logger.LogError(
                    "[PRICE-ANOMALY] Track {TrackId} license={LicenseType}: expected≥{Expected}c but charged={Actual}c " +
                    "for user {UserId} — issuing refund. Session={Session}",
                    trackId, licenseType, expectedCents, amountTotal.Value, userId, stripeSessionId);
                await IssueAutoRefundAsync(stripeSessionId, userId, trackId);
                return;
            }
            if (amountTotal.Value < expectedCents)
            {
                // Within tolerance — may happen if the creator changed the price between
                // checkout creation and webhook delivery. Log and proceed.
                _logger.LogWarning(
                    "[PRICE-WARNING] Track {TrackId} license={LicenseType}: expected={Expected}c charged={Actual}c " +
                    "for user {UserId}. Possible price change during session. Proceeding.",
                    trackId, licenseType, expectedCents, amountTotal.Value, userId);
            }
        }

        // H2: Buyer must not be the creator — prevents self-crediting fraud.
        if (string.Equals(track.CreatorId, userId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "[SELF-PURCHASE] Creator {UserId} attempted to purchase their own track {TrackId} — issuing refund. Session={Session}",
                userId, trackId, stripeSessionId);
            await IssueAutoRefundAsync(stripeSessionId, userId, trackId);
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
                _logger.LogWarning(
                    "Exclusive race: Track {TrackId} was sold by another request — initiating refund for user {UserId}.",
                    trackId, userId);
                await IssueAutoRefundAsync(stripeSessionId, userId, trackId);
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
                _logger.LogWarning(
                    "Copyright buyout race: Track {TrackId} was sold/transferred by another request — initiating refund for user {UserId}.",
                    trackId, userId);
                await IssueAutoRefundAsync(stripeSessionId, userId, trackId);
                return;
            }
        }

        // Prevent duplicate purchases for the same track/user/license
        var existingPurchase = await _db.Purchases
            .FirstOrDefaultAsync(p => p.BuyerId == userId && p.TrackId == trackId && p.LicenseType == licenseType);

        if (existingPurchase is not null)
        {
            _logger.LogInformation("Duplicate purchase skipped for user {UserId} track {TrackId}", userId, trackId);
            if (existingPurchase.Status != PurchaseStatuses.Completed)
            {
                existingPurchase.Status = PurchaseStatuses.Completed;
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
        // Stripe AmountTotal is long. Purchase.AmountCents is int (~$21.4M ceiling).
        // Refuse to silently truncate.
        //
        // Throw rather than return: a `return` here would let ProcessEventAsync
        // mark the webhook event as "completed" and ack 200 to Stripe — the buyer
        // would have paid but the purchase row, library item, license, and creator
        // credit would all be missing, and the failure would not appear in the
        // webhook ledger for operators to find. Throwing causes the outer handler
        // to mark the event as "failed", roll back the transaction, and re-throw
        // (Stripe receives 500 → retries within its 3-day window → operators see
        // the [DEAD-LETTER] log and can intervene before the retry budget runs out).
        var rawAmount = amountTotal ?? 0;
        if (rawAmount > int.MaxValue || rawAmount < 0)
        {
            _logger.LogError(
                "[DEAD-LETTER] Stripe purchase amount {Amount}c exceeds int.MaxValue or is negative; "
                + "refusing to truncate. User={UserId} Track={TrackId} Session={SessionId}. "
                + "Manual reconciliation + refund required.",
                rawAmount, userId, trackId, stripeSessionId);
            throw new InvalidOperationException(
                $"Stripe purchase amount {rawAmount}c is out of range (>int.MaxValue or negative). "
                + $"Session={stripeSessionId}. Manual reconciliation required.");
        }
        var safeAmountCents = (int)rawAmount;
        _logger.LogInformation("PURCHASE_TIMELINE: Creating purchase userId:{UserId} trackId:{TrackId} license:{LicenseType} amount:{AmountCents}c sessionId:{SessionId}",
            userId, trackId, licenseType, safeAmountCents, stripeSessionId);
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = userId,
            TrackId = trackId,
            AmountCents = safeAmountCents,
            PaymentMethod = "stripe",
            LicenseType = licenseType,
            UsageType = usageType,
            Status = PurchaseStatuses.Completed,
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
            // Single source of truth: CreatorEarningsCalculator.
            var creatorCents = CreatorEarningsCalculator.ComputeCreatorCents(grossCents, platformFeeRate);

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

        // ── Send purchase confirmation email ──
        try
        {
            var buyer = await _db.Users.FindAsync(userId);
            if (buyer?.Email is not null)
            {
                var frontendUrl = _config["App:FrontendUrl"]?.TrimEnd('/') ?? "";
                var licenseUrl = $"{frontendUrl}/hub/licenses/{purchase.LicenseId}";
                await _email.SendPurchaseConfirmationAsync(
                    buyer.Email,
                    track.Title,
                    licenseType,
                    (amountTotal ?? 0) / 100m,
                    licenseUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send purchase confirmation email for purchase {PurchaseId} — non-critical", purchase.Id);
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
    private async Task HandleSubscriptionCheckout(string userId, string tier, string? stripeCustomerId)
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
            StripeCustomerId = stripeCustomerId,
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
    /// Handle invoice.paid — restore subscription health after a successful renewal payment.
    /// Uses the locally-stored StripeCustomerId when available so webhook processing
    /// does not depend on a live Stripe customer lookup.
    /// </summary>
    private async Task HandleInvoicePaid(string? stripeCustomerId)
    {
        if (string.IsNullOrEmpty(stripeCustomerId))
        {
            _logger.LogWarning("invoice.paid received without customer ID");
            return;
        }

        var user = await FindUserByStripeCustomerAsync(stripeCustomerId);
        if (user is null)
        {
            _logger.LogWarning(
                "invoice.paid: could not match Stripe customer {CustomerId} to a local user.",
                stripeCustomerId);
            return;
        }

        var latestSubscription = await _db.Subscriptions
            .Where(s => s.UserId == user.Id)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync();

        if (latestSubscription is not null)
        {
            latestSubscription.Status = "active";
            latestSubscription.StripeCustomerId ??= stripeCustomerId;
            if (latestSubscription.ExpiresAt is null || latestSubscription.ExpiresAt < DateTime.UtcNow)
            {
                latestSubscription.ExpiresAt = DateTime.UtcNow.AddMonths(1);
            }
        }

        user.SubscriptionStatus = "Active";
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Invoice paid: User={UserId} StripeCustomer={CustomerId} marked subscription active",
            user.Id, stripeCustomerId);
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
    /// Handle payment_intent.succeeded — recorded for observability only.
    /// Checkout fulfillment remains keyed off checkout.session.completed.
    /// </summary>
    private Task HandlePaymentIntentSucceeded(string? stripePaymentIntentId)
    {
        if (string.IsNullOrEmpty(stripePaymentIntentId))
        {
            _logger.LogWarning("payment_intent.succeeded received without payment intent ID");
        }
        else
        {
            _logger.LogInformation(
                "payment_intent.succeeded acknowledged for PaymentIntent {PaymentIntentId}",
                stripePaymentIntentId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Handle charge.refunded — revoke access for refunded purchases.
    /// Looks up the purchase by StripeSessionId (via PaymentIntent → Session).
    /// Exceptions propagate to the outer transaction handler so the event is retried.
    /// </summary>
    private async Task HandleChargeRefunded(string? stripePaymentIntentId)
    {
        if (string.IsNullOrEmpty(stripePaymentIntentId))
        {
            _logger.LogWarning("charge.refunded received without payment_intent ID");
            return;
        }

        // Look up the checkout session via the payment intent.
        // If this Stripe API call fails, the exception propagates and Stripe will retry.
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

        purchase.Status = PurchaseStatuses.Refunded;
        purchase.UpdatedAt = DateTime.UtcNow;

        // Remove library access
        var libraryItem = await _db.Library
            .FirstOrDefaultAsync(l => l.UserId == purchase.BuyerId && l.TrackId == purchase.TrackId);
        if (libraryItem is not null)
            _db.Library.Remove(libraryItem);

        // SECURITY: Claw back creator wallet credit.
        // Guard against double-clawback when the webhook fires twice.
        var alreadyClawedBack = await _db.WalletTransactions
            .AnyAsync(wt => wt.RelatedPurchaseId == purchase.Id && wt.Type == "debit"
                && wt.Description != null && wt.Description.StartsWith("Refund clawback:"));

        if (!alreadyClawedBack)
        {
            // Prefer the stored credit amount (exact match to what was credited).
            // Fall back to authoritative re-computation if no credit record exists.
            var originalCredit = await _db.WalletTransactions
                .FirstOrDefaultAsync(wt => wt.RelatedPurchaseId == purchase.Id && wt.Type == "credit");

            string? clawbackUserId;
            long clawbackCents;

            if (originalCredit is not null)
            {
                clawbackUserId = originalCredit.UserId;
                clawbackCents = originalCredit.AmountCents; // already positive
            }
            else
            {
                // Authoritative fallback: compute from purchase amount + creator's current tier.
                var track = await _db.Tracks.FirstOrDefaultAsync(t => t.Id == purchase.TrackId);
                clawbackUserId = track?.CreatorId;
                if (string.IsNullOrEmpty(clawbackUserId))
                {
                    _logger.LogWarning(
                        "Refund clawback skipped: no credit record and no track creator for purchase {PurchaseId}. Manual reconciliation required.",
                        purchase.Id);
                    clawbackCents = 0;
                }
                else
                {
                    var creatorUser = await _db.Users.FindAsync(clawbackUserId);
                    var feeRate = creatorUser is not null
                        ? TierManifest.For(creatorUser.CreatorTier).FeeRate
                        : TierManifest.Free.FeeRate;
                    // Single source of truth: must match the credit math the original sale used.
                    clawbackCents = CreatorEarningsCalculator.ComputeCreatorCents(purchase.AmountCents, feeRate);
                    _logger.LogWarning(
                        "Refund clawback: no original credit transaction found for purchase {PurchaseId}; computed {Cents}c from purchase amount.",
                        purchase.Id, clawbackCents);
                }
            }

            if (!string.IsNullOrEmpty(clawbackUserId) && clawbackCents > 0)
            {
                // Concurrency guard: under default ReadCommitted isolation, two refund
                // webhooks for two different purchases by the same creator could both
                // observe the same `currentBalance` and both proceed to debit, still
                // pushing the wallet negative. Take a row-level lock on the creator's
                // user row so any other refund/dispute clawback for the same creator
                // serializes behind us. The lock is released on outer-tx commit/rollback.
                if (_db.Database.IsRelational())
                {
                    await _db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = {clawbackUserId} FOR UPDATE");
                }

                // Underflow guard: if the creator has already withdrawn the credit
                // (or their balance is otherwise lower than the clawback), debit only
                // what is actually present and emit a [RECONCILE] log so finance can
                // recover the shortfall manually. We never push the wallet below 0.
                var currentBalance = await _db.WalletTransactions
                    .Where(w => w.UserId == clawbackUserId)
                    .SumAsync(w => w.AmountCents);

                var effectiveClawback = Math.Min(clawbackCents, Math.Max(0L, currentBalance));
                var shortfall = clawbackCents - effectiveClawback;

                if (shortfall > 0)
                {
                    _logger.LogError(
                        "[RECONCILE] Refund clawback shortfall for purchase {PurchaseId}: " +
                        "owed {Owed}c, balance {Balance}c, debiting {Effective}c, shortfall {Shortfall}c. " +
                        "Manual recovery required for creator {CreatorId}.",
                        purchase.Id, clawbackCents, currentBalance, effectiveClawback, shortfall, clawbackUserId);
                }

                if (effectiveClawback > 0)
                {
                    _db.WalletTransactions.Add(new WalletTransaction
                    {
                        Id = Guid.NewGuid(),
                        UserId = clawbackUserId,
                        AmountCents = -effectiveClawback,
                        Type = "debit",
                        Description = $"Refund clawback: {purchase.Id}",
                        RelatedPurchaseId = purchase.Id,
                        CreatedAt = DateTime.UtcNow
                    });
                    _logger.LogInformation(
                        "Wallet clawback: debited {AmountCents}c from creator {CreatorId} for refunded purchase {PurchaseId}",
                        effectiveClawback, clawbackUserId, purchase.Id);
                }
            }
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation(
            "Refund processed: PurchaseId={PurchaseId} UserId={UserId} TrackId={TrackId}",
            purchase.Id, purchase.BuyerId, purchase.TrackId);
    }

    /// <summary>
    /// Handle charge.dispute.created — flag the purchase as disputed for review.
    /// Exceptions propagate to the outer transaction handler so the event is retried.
    /// </summary>
    private async Task HandleChargeDisputeCreated(string? stripePaymentIntentId)
    {
        if (string.IsNullOrEmpty(stripePaymentIntentId))
        {
            _logger.LogWarning("charge.dispute.created received without payment_intent ID");
            return;
        }

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

        purchase.Status = PurchaseStatuses.Disputed;
        purchase.UpdatedAt = DateTime.UtcNow;

        // SECURITY: Claw back creator wallet credit on dispute.
        // Guard against double-clawback if webhook fires twice.
        var alreadyClawedBack = await _db.WalletTransactions
            .AnyAsync(wt => wt.RelatedPurchaseId == purchase.Id && wt.Type == "debit"
                && wt.Description != null && wt.Description.StartsWith("Dispute clawback:"));

        if (!alreadyClawedBack)
        {
            var disputeCredit = await _db.WalletTransactions
                .FirstOrDefaultAsync(wt => wt.RelatedPurchaseId == purchase.Id && wt.Type == "credit");

            if (disputeCredit is not null)
            {
                // Concurrency guard: serialize concurrent dispute/refund clawbacks for
                // the same creator via a row-level lock on AspNetUsers, so the balance
                // read below cannot race with another in-flight clawback transaction.
                if (_db.Database.IsRelational())
                {
                    await _db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = {disputeCredit.UserId} FOR UPDATE");
                }

                // Underflow guard — never push the wallet below 0. Surface the shortfall
                // for manual recovery via [RECONCILE] log.
                var owedCents = disputeCredit.AmountCents;
                var currentBalance = await _db.WalletTransactions
                    .Where(w => w.UserId == disputeCredit.UserId)
                    .SumAsync(w => w.AmountCents);
                var effectiveClawback = Math.Min(owedCents, Math.Max(0L, currentBalance));
                var shortfall = owedCents - effectiveClawback;

                if (shortfall > 0)
                {
                    _logger.LogError(
                        "[RECONCILE] Dispute clawback shortfall for purchase {PurchaseId}: " +
                        "owed {Owed}c, balance {Balance}c, debiting {Effective}c, shortfall {Shortfall}c. " +
                        "Manual recovery required for creator {CreatorId}.",
                        purchase.Id, owedCents, currentBalance, effectiveClawback, shortfall, disputeCredit.UserId);
                }

                if (effectiveClawback > 0)
                {
                    _db.WalletTransactions.Add(new WalletTransaction
                    {
                        Id = Guid.NewGuid(),
                        UserId = disputeCredit.UserId,
                        AmountCents = -effectiveClawback,
                        Type = "debit",
                        Description = $"Dispute clawback: {purchase.Id}",
                        RelatedPurchaseId = purchase.Id,
                        CreatedAt = DateTime.UtcNow
                    });
                    _logger.LogInformation(
                        "Wallet clawback: debited {AmountCents}c from creator {CreatorId} for disputed purchase {PurchaseId}",
                        effectiveClawback, disputeCredit.UserId, purchase.Id);
                }
            }
        }

        await _db.SaveChangesAsync();

        _logger.LogWarning(
            "Dispute opened: PurchaseId={PurchaseId} UserId={UserId} TrackId={TrackId} PaymentIntent={PI}",
            purchase.Id, purchase.BuyerId, purchase.TrackId, stripePaymentIntentId);
    }

    /// <summary>
    /// Attempt to match a Stripe customer ID to a local ApplicationUser by looking up
    /// the customer's email in Stripe and matching it to our users table.
    /// </summary>
    private async Task<ApplicationUser?> FindUserByStripeCustomerAsync(string stripeCustomerId)
    {
        var localUserId = await _db.Subscriptions
            .Where(s => s.StripeCustomerId == stripeCustomerId)
            .OrderByDescending(s => s.StartedAt)
            .Select(s => s.UserId)
            .FirstOrDefaultAsync();

        if (!string.IsNullOrEmpty(localUserId))
        {
            return await _db.Users.FindAsync(localUserId);
        }

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

    /// <summary>
    /// Issue an automatic Stripe refund for the losing party in an exclusive-sale or
    /// copyright-buyout race condition. The DB already rejected their purchase; this
    /// ensures they are not silently charged without receiving the product.
    /// Errors are caught and logged — failure here does NOT re-throw because we must
    /// not mark the webhook as failed (which would trigger a Stripe retry loop).
    /// </summary>
    private async Task IssueAutoRefundAsync(string? stripeSessionId, string userId, Guid trackId)
    {
        if (string.IsNullOrWhiteSpace(stripeSessionId))
        {
            _logger.LogError(
                "[AUTO-REFUND-FAILED] No session ID available — manual refund required. " +
                "User={UserId} Track={TrackId}",
                userId, trackId);
            return;
        }

        try
        {
            var session = await new SessionService().GetAsync(stripeSessionId);

            if (string.IsNullOrEmpty(session?.PaymentIntentId))
            {
                _logger.LogError(
                    "[AUTO-REFUND-FAILED] Session {SessionId} has no PaymentIntentId — manual refund required. " +
                    "User={UserId} Track={TrackId}",
                    stripeSessionId, userId, trackId);
                return;
            }

            await new RefundService().CreateAsync(new RefundCreateOptions
            {
                PaymentIntent = session.PaymentIntentId,
                Reason = RefundReasons.Duplicate
            });

            _logger.LogInformation(
                "[AUTO-REFUND] Exclusive race loser refunded successfully. " +
                "User={UserId} Track={TrackId} Session={SessionId}",
                userId, trackId, stripeSessionId);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex,
                "[AUTO-REFUND-FAILED] Stripe error — manual refund required. " +
                "User={UserId} Track={TrackId} Session={SessionId}",
                userId, trackId, stripeSessionId);
        }
    }
}
