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
    private const string EventSubscriptionUpdated = "customer.subscription.updated";
    private const string EventInvoicePaid = "invoice.paid";
    private const string EventInvoicePaymentFailed = "invoice.payment_failed";
    private const string EventPaymentIntentSucceeded = "payment_intent.succeeded";
    private const string EventChargeRefunded = "charge.refunded";
    private const string EventChargeDisputeCreated = "charge.dispute.created";
    private readonly CambrianDbContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _config;
    private readonly string _webhookSecret;
    private readonly ILogger<StripeWebhookService> _logger;
    private readonly IAuthorshipRecordIssuer? _authorshipIssuer;

    public StripeWebhookService(
        CambrianDbContext db,
        IEmailService email,
        IConfiguration configuration,
        ILogger<StripeWebhookService> logger,
        IHostEnvironment env,
        IAuthorshipRecordIssuer? authorshipIssuer = null)
    {
        _db = db;
        _email = email;
        _config = configuration;
        _webhookSecret = configuration["Stripe:WebhookSecret"] ?? "";
        _logger = logger;
        _authorshipIssuer = authorshipIssuer;
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
        string? stripeSubscriptionId = null;

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
                stripeSubscriptionId = session?.SubscriptionId;
            }
            else if (eventType == EventSubscriptionDeleted)
            {
                var sub = stripeEvent.Data.Object as global::Stripe.Subscription;
                stripeCustomerId = sub?.CustomerId;
            }
            else if (eventType == EventSubscriptionUpdated)
            {
                var sub = stripeEvent.Data.Object as global::Stripe.Subscription;
                stripeCustomerId = sub?.CustomerId;
                stripeSubscriptionId = sub?.Id;
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
        await ProcessEventAsync(eventId, eventType, clientReferenceId, amountTotal, stripeCustomerId, stripeSessionId, stripePaymentIntentId, payload, stripeSubscriptionId);
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
        string? payload = null,
        string? stripeSubscriptionId = null)
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
            Cambrian.Application.Observability.CambrianMetrics.WebhookDuplicate.Add(1);
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
                await HandleCheckoutCompleted(clientReferenceId, amountTotal, stripeSessionId, stripeCustomerId, stripeSubscriptionId);
            }
            else if (eventType == EventSubscriptionDeleted)
            {
                await HandleSubscriptionDeleted(stripeCustomerId);
            }
            else if (eventType == EventSubscriptionUpdated)
            {
                await HandleSubscriptionUpdated(stripeCustomerId, stripeSubscriptionId, payload);
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
            Cambrian.Application.Observability.CambrianMetrics.WebhookProcessed.Add(1);
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
                Cambrian.Application.Observability.CambrianMetrics.WebhookFailed.Add(1);
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

    private async Task HandleCheckoutCompleted(string? clientReferenceId, long? amountTotal, string? stripeSessionId, string? stripeCustomerId, string? stripeSubscriptionId = null)
    {
        if (clientReferenceId is null)
        {
            _logger.LogError("[DEAD-LETTER] Checkout session completed but no ClientReferenceId — paid session cannot be fulfilled. StripeSessionId={SessionId}", stripeSessionId);
            return;
        }

        // BillingController sets clientReferenceId = "userId:subscription:tier".
        // Track-license purchasing has been removed, so subscription checkout is the
        // only fulfillment path remaining here.
        var parts = clientReferenceId.Split(':');
        if (parts.Length >= 3 && parts[1] == "subscription")
        {
            await HandleSubscriptionCheckout(parts[0], parts[2], stripeCustomerId, stripeSubscriptionId);
            return;
        }

        // AuthorshipRecordService sets clientReferenceId = "userId:authorship:recordId".
        // Payment completes the record: hash evidence, freeze + sign the canonical JSON.
        if (parts.Length == 3 && parts[1] == "authorship" && Guid.TryParse(parts[2], out var recordId))
        {
            if (_authorshipIssuer is null)
            {
                _logger.LogError(
                    "[DEAD-LETTER] Authorship payment received but no issuer is registered. RecordId={RecordId} StripeSessionId={SessionId}",
                    recordId, stripeSessionId);
                return;
            }

            await _authorshipIssuer.IssueForSessionAsync(recordId, stripeSessionId ?? "");
            return;
        }

        _logger.LogWarning(
            "[IGNORED] checkout.session.completed with non-subscription clientReferenceId '{Ref}' — " +
            "track-license purchasing has been removed; nothing to fulfill. StripeSessionId={SessionId}",
            clientReferenceId, stripeSessionId);
    }


    /// <summary>
    /// Handle a subscription checkout: create or update the user's Subscription record
    /// and upgrade their tier on the ApplicationUser.
    /// </summary>
    private async Task HandleSubscriptionCheckout(string userId, string tier, string? stripeCustomerId, string? stripeSubscriptionId = null)
    {
        // Normalize the tier slug to a known tier config (creator/pro/free).
        var tierConfig = TierManifest.For(tier);
        tier = tierConfig.Slug;

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
            StripeSubscriptionId = stripeSubscriptionId,
            StartedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMonths(1)
        };
        _db.Subscriptions.Add(subscription);

        // Update user tier from the tier manifest (free/creator/pro all map correctly).
        var user = await _db.Users.FindAsync(userId);
        if (user is not null)
        {
            user.Tier = tier;
            user.CreatorTier = tierConfig.Tier;
            user.SubscriptionStatus = "Active";
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Subscription activated: User={UserId} Plan={Plan}",
            userId, tier);
        Cambrian.Application.Observability.CambrianMetrics.CheckoutCompleted.Add(1);
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
    /// Handle customer.subscription.updated — sync status, plan, and period end after a
    /// portal-driven upgrade/downgrade or any Stripe-side subscription state change.
    /// The plan is derived from the subscription's price id (matched against the configured
    /// Stripe price ids). Fields are read from the signed payload so this stays correct across
    /// Stripe.net releases that relocate current_period_end onto subscription items.
    /// </summary>
    private async Task HandleSubscriptionUpdated(string? stripeCustomerId, string? stripeSubscriptionId, string? payload)
    {
        string? status = null;
        string? priceId = null;
        long? periodEndUnix = null;
        var customerId = stripeCustomerId;

        if (!string.IsNullOrEmpty(payload))
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("object", out var obj))
                {
                    customerId ??= obj.TryGetProperty("customer", out var c) ? c.GetString() : null;
                    status = obj.TryGetProperty("status", out var s) ? s.GetString() : null;
                    periodEndUnix = ExtractPeriodEnd(obj);
                    priceId = ExtractPriceId(obj);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "customer.subscription.updated: failed to parse payload");
            }
        }

        if (string.IsNullOrEmpty(customerId))
        {
            _logger.LogWarning("customer.subscription.updated: no customer id resolved");
            return;
        }

        var user = await FindUserByStripeCustomerAsync(customerId);
        if (user is null)
        {
            _logger.LogWarning(
                "customer.subscription.updated: could not match Stripe customer {CustomerId} to a local user.",
                customerId);
            return;
        }

        var sub = await _db.Subscriptions
            .Where(s => s.UserId == user.Id)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync();

        var tierSlug = MapPriceToTierSlug(priceId);            // null = keep existing plan
        var userStatus = MapStripeStatusToUser(status);
        var subStatus = MapStripeStatusToSub(status);
        var expiresAt = periodEndUnix is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(periodEndUnix.Value).UtcDateTime
            : (DateTime?)null;

        if (sub is not null)
        {
            sub.Status = subStatus;
            if (tierSlug is not null) sub.Plan = tierSlug;
            if (!string.IsNullOrEmpty(stripeSubscriptionId)) sub.StripeSubscriptionId ??= stripeSubscriptionId;
            sub.StripeCustomerId ??= customerId;
            if (expiresAt is not null) sub.ExpiresAt = expiresAt;
        }

        if (tierSlug is not null)
        {
            var tierConfig = TierManifest.For(tierSlug);
            user.Tier = tierConfig.Slug;
            user.CreatorTier = tierConfig.Tier;
        }
        user.SubscriptionStatus = userStatus;

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Subscription updated via webhook: User={UserId} StripeCustomer={CustomerId} plan={Plan} status={Status}",
            user.Id, customerId, tierSlug ?? "(unchanged)", userStatus);
    }

    /// <summary>Match a Stripe price id to a tier slug using configured price ids.</summary>
    private string? MapPriceToTierSlug(string? priceId)
    {
        if (string.IsNullOrWhiteSpace(priceId)) return null;
        if (priceId == _config["Stripe:Prices:Creator"]) return "creator";
        if (priceId == _config["Stripe:Prices:Pro"]) return "pro";
        return null;
    }

    private static string MapStripeStatusToUser(string? stripeStatus) => (stripeStatus ?? "").ToLowerInvariant() switch
    {
        "active" or "trialing" => "Active",
        "past_due" or "unpaid" or "incomplete" => "PastDue",
        "canceled" or "incomplete_expired" => "Cancelled",
        _ => "Active"
    };

    private static string MapStripeStatusToSub(string? stripeStatus) => (stripeStatus ?? "").ToLowerInvariant() switch
    {
        "canceled" or "incomplete_expired" => "cancelled",
        _ => "active"
    };

    private static string? ExtractPriceId(JsonElement obj)
    {
        if (obj.TryGetProperty("items", out var items) &&
            items.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array &&
            data.GetArrayLength() > 0)
        {
            var first = data[0];
            if (first.TryGetProperty("price", out var price) &&
                price.TryGetProperty("id", out var id))
                return id.GetString();
        }
        return null;
    }

    private static long? ExtractPeriodEnd(JsonElement obj)
    {
        // Newer Stripe API versions expose current_period_end on each item; older ones on the subscription.
        if (obj.TryGetProperty("current_period_end", out var topLevel) && topLevel.ValueKind == JsonValueKind.Number)
            return topLevel.GetInt64();

        if (obj.TryGetProperty("items", out var items) &&
            items.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array &&
            data.GetArrayLength() > 0 &&
            data[0].TryGetProperty("current_period_end", out var itemEnd) &&
            itemEnd.ValueKind == JsonValueKind.Number)
            return itemEnd.GetInt64();

        return null;
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

}
