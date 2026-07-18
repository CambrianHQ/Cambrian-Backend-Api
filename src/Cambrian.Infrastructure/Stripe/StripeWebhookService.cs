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
    private const string EventSubscriptionTrialWillEnd = "customer.subscription.trial_will_end";
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
    private readonly IPurchaseAnalyticsService? _purchaseAnalytics;

    public StripeWebhookService(
        CambrianDbContext db,
        IEmailService email,
        IConfiguration configuration,
        ILogger<StripeWebhookService> logger,
        IHostEnvironment env,
        IAuthorshipRecordIssuer? authorshipIssuer = null,
        IPurchaseAnalyticsService? purchaseAnalytics = null)
    {
        _db = db;
        _email = email;
        _config = configuration;
        _webhookSecret = configuration["Stripe:WebhookSecret"] ?? "";
        _logger = logger;
        _authorshipIssuer = authorshipIssuer;
        _purchaseAnalytics = purchaseAnalytics;
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
        string? paymentStatus = null;
        string? currency = null;
        string? invoiceId = null;
        DateTime? invoicePeriodEnd = null;
        bool? chargeFullyRefunded = null;
        long? chargeAmountRefunded = null;

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
            // Stripe.net pins an expected API version; events delivered under a newer
            // account/CLI API version must not hard-fail signature construction. The fields
            // we read (id, type, customer, subscription, client_reference_id, metadata,
            // amounts) are stable across these versions, so we disable the version throw.
            var stripeEvent = EventUtility.ConstructEvent(payload, signature, _webhookSecret, throwOnApiVersionMismatch: false);
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
            else if (eventType == EventSubscriptionTrialWillEnd)
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
            (eventId, eventType, clientReferenceId, amountTotal, stripeCustomerId, stripeSessionId, stripePaymentIntentId, stripeSubscriptionId)
                = ParseSignedEventFallback(payload);
        }

        var signedContext = ParseSignedEventContext(payload);
        stripeCustomerId ??= signedContext.CustomerId;
        stripeSubscriptionId ??= signedContext.SubscriptionId;
        stripePaymentIntentId ??= signedContext.PaymentIntentId;
        paymentStatus = signedContext.PaymentStatus;
        currency = signedContext.Currency;
        invoiceId = signedContext.InvoiceId;
        invoicePeriodEnd = signedContext.InvoicePeriodEnd;
        chargeFullyRefunded = signedContext.ChargeFullyRefunded;
        chargeAmountRefunded = signedContext.ChargeAmountRefunded;

        _logger.LogInformation("Stripe webhook verified: {EventType} {EventId}", eventType, eventId);
        await ProcessEventAsync(eventId, eventType, clientReferenceId, amountTotal, stripeCustomerId,
            stripeSessionId, stripePaymentIntentId, payload, stripeSubscriptionId,
            paymentStatus, currency, invoiceId, invoicePeriodEnd,
            chargeFullyRefunded, chargeAmountRefunded, requireProviderProof: true);
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
        string? StripeCustomerId, string? StripeSessionId, string? StripePaymentIntentId, string? StripeSubscriptionId)
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
        string? stripeSubscriptionId = null;
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
                if (obj.TryGetProperty("subscription", out var subscriptionProp))
                {
                    stripeSubscriptionId = subscriptionProp.ValueKind == JsonValueKind.String
                        ? subscriptionProp.GetString()
                        : subscriptionProp.TryGetProperty("id", out var subscriptionIdProp) ? subscriptionIdProp.GetString() : null;
                }
            }
            else if (eventType is EventSubscriptionDeleted or EventSubscriptionUpdated or EventSubscriptionTrialWillEnd)
            {
                stripeCustomerId = obj.TryGetProperty("customer", out var customerProp) ? customerProp.GetString() : null;
                stripeSubscriptionId = obj.TryGetProperty("id", out var subscriptionProp) ? subscriptionProp.GetString() : null;
            }
            else if (eventType is EventInvoicePaid or EventInvoicePaymentFailed)
            {
                stripeCustomerId = obj.TryGetProperty("customer", out var customerProp) ? customerProp.GetString() : null;
            }
            else if (eventType is EventPaymentIntentSucceeded or EventChargeRefunded or EventChargeDisputeCreated)
            {
                stripePaymentIntentId = obj.TryGetProperty("payment_intent", out var piProp) ? piProp.GetString() : null;
                stripePaymentIntentId ??= obj.TryGetProperty("id", out var intentProp) ? intentProp.GetString() : null;
            }
        }

        return (eventId, eventType, clientReferenceId, amountTotal, stripeCustomerId, stripeSessionId, stripePaymentIntentId, stripeSubscriptionId);
    }

    private sealed record SignedEventContext(
        string? CustomerId,
        string? SubscriptionId,
        string? PaymentIntentId,
        string? PaymentStatus,
        string? Currency,
        string? InvoiceId,
        DateTime? InvoicePeriodEnd,
        bool? ChargeFullyRefunded,
        long? ChargeAmountRefunded);

    private static SignedEventContext ParseSignedEventContext(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var eventType = root.GetProperty("type").GetString() ?? string.Empty;
        var obj = root.GetProperty("data").GetProperty("object");

        var customerId = ReadStringOrId(obj, "customer");
        var paymentIntentId = ReadStringOrId(obj, "payment_intent");
        var subscriptionId = ReadStringOrId(obj, "subscription");
        var paymentStatus = obj.TryGetProperty("payment_status", out var paymentStatusProp)
            ? paymentStatusProp.GetString()
            : null;
        var currency = obj.TryGetProperty("currency", out var currencyProp)
            ? currencyProp.GetString()
            : null;
        string? invoiceId = null;
        DateTime? invoicePeriodEnd = null;
        bool? fullyRefunded = null;
        long? amountRefunded = null;

        if (eventType is EventInvoicePaid or EventInvoicePaymentFailed)
        {
            invoiceId = ReadStringOrId(obj, "id");
            subscriptionId ??= ExtractInvoiceSubscriptionId(obj);
            invoicePeriodEnd = ExtractInvoicePeriodEnd(obj);
        }
        else if (eventType is EventSubscriptionDeleted or EventSubscriptionUpdated or EventSubscriptionTrialWillEnd)
        {
            subscriptionId ??= ReadStringOrId(obj, "id");
        }
        else if (eventType == EventChargeRefunded)
        {
            fullyRefunded = obj.TryGetProperty("refunded", out var refundedProp)
                && refundedProp.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? refundedProp.GetBoolean()
                : null;
            amountRefunded = obj.TryGetProperty("amount_refunded", out var amountProp)
                && amountProp.ValueKind == JsonValueKind.Number
                ? amountProp.GetInt64()
                : null;
        }
        else if (eventType == EventChargeDisputeCreated
            && obj.TryGetProperty("charge", out var charge)
            && charge.ValueKind == JsonValueKind.Object)
        {
            customerId ??= ReadStringOrId(charge, "customer");
            paymentIntentId ??= ReadStringOrId(charge, "payment_intent");
        }

        return new SignedEventContext(customerId, subscriptionId, paymentIntentId,
            paymentStatus, currency, invoiceId, invoicePeriodEnd, fullyRefunded, amountRefunded);
    }

    private static string? ReadStringOrId(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("id", out var id)
            ? id.GetString()
            : null;
    }

    private static string? ExtractInvoiceSubscriptionId(JsonElement invoice)
    {
        if (invoice.TryGetProperty("parent", out var parent)
            && parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty("subscription_details", out var details)
            && details.ValueKind == JsonValueKind.Object)
        {
            return ReadStringOrId(details, "subscription");
        }
        return null;
    }

    private static DateTime? ExtractInvoicePeriodEnd(JsonElement invoice)
    {
        if (!invoice.TryGetProperty("lines", out var lines)
            || !lines.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
            return null;

        long? latest = null;
        foreach (var line in data.EnumerateArray())
        {
            if (line.TryGetProperty("period", out var period)
                && period.TryGetProperty("end", out var end)
                && end.ValueKind == JsonValueKind.Number)
            {
                var unix = end.GetInt64();
                latest = !latest.HasValue || unix > latest ? unix : latest;
            }
        }
        return latest is > 0 ? DateTimeOffset.FromUnixTimeSeconds(latest.Value).UtcDateTime : null;
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
        string? stripeSubscriptionId = null,
        string? paymentStatus = "paid",
        string? currency = "usd",
        string? invoiceId = null,
        DateTime? invoicePeriodEnd = null,
        bool? chargeFullyRefunded = null,
        long? chargeAmountRefunded = null,
        bool requireProviderProof = false)
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

        if (!string.IsNullOrWhiteSpace(payload))
        {
            var context = ParseSignedEventContext(payload);
            stripeCustomerId ??= context.CustomerId;
            stripeSubscriptionId ??= context.SubscriptionId;
            stripePaymentIntentId ??= context.PaymentIntentId;
            invoiceId ??= context.InvoiceId;
            invoicePeriodEnd ??= context.InvoicePeriodEnd;
            chargeFullyRefunded ??= context.ChargeFullyRefunded;
            chargeAmountRefunded ??= context.ChargeAmountRefunded;
            if (requireProviderProof)
            {
                paymentStatus = context.PaymentStatus;
                currency = context.Currency;
            }
        }

        var alreadyCompleted = await _db.StripeWebhookEvents
            .AnyAsync(e => e.EventId == eventId && e.Status == "completed");

        if (alreadyCompleted)
        {
            _logger.LogInformation("EVENT: webhook_duplicate eventId:{EventId}", eventId);
            Cambrian.Application.Observability.CambrianMetrics.WebhookDuplicate.Add(1);
            return;
        }

        // ── Step 3: Begin transaction FIRST — event row and all business effects commit atomically ──
        var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;

        // PostgreSQL transaction-scoped claim: duplicate and retry deliveries for the
        // same event serialize before any fulfillment read/write. The durable unique
        // EventId index remains the final database backstop.
        if (transaction is not null
            && _db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({normalizedEventId}, 0))");

            if (await _db.StripeWebhookEvents.AsNoTracking()
                .AnyAsync(e => e.EventId == normalizedEventId && e.Status == "completed"))
            {
                await transaction.CommitAsync();
                _logger.LogInformation("EVENT: webhook_duplicate eventId:{EventId} reason:claimed_completed", normalizedEventId);
                Cambrian.Application.Observability.CambrianMetrics.WebhookDuplicate.Add(1);
                return;
            }
        }

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

        PurchaseAnalyticsEvent? purchaseAnalyticsEvent = null;

        try
        {
            // ── Step 4: Process inside transaction ──
            if (eventType == EventTypes.CheckoutSessionCompleted)
            {
                purchaseAnalyticsEvent = await HandleCheckoutCompleted(
                    normalizedEventId, clientReferenceId, amountTotal, stripeSessionId, stripeCustomerId,
                    stripeSubscriptionId, stripePaymentIntentId, paymentStatus, currency, payload, requireProviderProof);
            }
            else if (eventType == EventSubscriptionDeleted)
            {
                purchaseAnalyticsEvent = await HandleSubscriptionDeleted(normalizedEventId, stripeCustomerId, stripeSubscriptionId);
            }
            else if (eventType == EventSubscriptionUpdated)
            {
                await HandleSubscriptionUpdated(stripeCustomerId, stripeSubscriptionId, payload);
            }
            else if (eventType == EventSubscriptionTrialWillEnd)
            {
                await HandleSubscriptionTrialWillEnd(stripeCustomerId, stripeSubscriptionId, payload);
            }
            else if (eventType == EventInvoicePaid)
            {
                await HandleInvoicePaid(stripeCustomerId, stripeSubscriptionId, invoiceId, invoicePeriodEnd);
            }
            else if (eventType == EventInvoicePaymentFailed)
            {
                await HandleInvoicePaymentFailed(stripeCustomerId, stripeSubscriptionId, invoiceId);
            }
            else if (eventType == EventPaymentIntentSucceeded)
            {
                await HandlePaymentIntentSucceeded(stripePaymentIntentId);
            }
            else if (eventType == EventChargeRefunded)
            {
                await HandleChargeRefunded(stripePaymentIntentId, stripeCustomerId, chargeFullyRefunded, chargeAmountRefunded);
            }
            else if (eventType == EventChargeDisputeCreated)
            {
                await HandleChargeDisputeCreated(stripePaymentIntentId, stripeCustomerId);
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

            _logger.LogInformation("EVENT: webhook_processed eventId:{EventId} eventType:{EventType}", normalizedEventId, eventType);
            Cambrian.Application.Observability.CambrianMetrics.WebhookProcessed.Add(1);
            CapturePurchaseAnalytics(purchaseAnalyticsEvent);
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

            // Concurrent duplicate delivery: Stripe (or a doubled forwarder/retry) can
            // deliver the same event twice close together. Both requests pass the
            // "already-completed" check (neither has committed yet) and both INSERT the
            // event row, so the loser hits a unique-violation on IX_StripeWebhookEvents_EventId.
            // That is benign — the concurrent winner owns processing — so treat it as a
            // duplicate and return success. Otherwise this 500s and Stripe retries forever.
            if (IsDuplicateEventInsert(ex))
            {
                _logger.LogInformation(
                    "EVENT: webhook_duplicate eventId:{EventId} eventType:{EventType} reason:concurrent_insert",
                    normalizedEventId, eventType);
                Cambrian.Application.Observability.CambrianMetrics.WebhookDuplicate.Add(1);
                return;
            }

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

    /// <summary>
    /// True when the exception chain is a Postgres unique-violation (SQLSTATE 23505)
    /// on one of our idempotency guards — the webhook event row
    /// (IX_StripeWebhookEvents_EventId) or a fulfillment dedup key keyed by the Stripe
    /// session/event (e.g. ux_release_credit_purchases_session). These fire only when a
    /// concurrent/duplicate delivery already processed the same event, so the work is
    /// already done — treat as a benign duplicate and return success instead of 500
    /// (which would make Stripe retry forever). Matched on the message to avoid a hard
    /// Npgsql dependency here.
    /// </summary>
    private static bool IsDuplicateEventInsert(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var m = e.Message;
            var isUniqueViolation = m.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || m.Contains("23505", StringComparison.Ordinal);
            if (!isUniqueViolation) continue;

            // Idempotency guards keyed by the Stripe event / session. A collision here
            // means another delivery already fulfilled this event.
            if (m.Contains("IX_StripeWebhookEvents_EventId", StringComparison.OrdinalIgnoreCase)
                || m.Contains("_session", StringComparison.OrdinalIgnoreCase)
                || m.Contains("_event", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private void CapturePurchaseAnalytics(PurchaseAnalyticsEvent? purchaseEvent)
    {
        if (purchaseEvent is null || _purchaseAnalytics is null)
            return;

        try
        {
            _ = _purchaseAnalytics.CaptureAsync(purchaseEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Purchase analytics dispatch failed open for {EventName}.",
                purchaseEvent.EventName);
        }
    }

    private async Task<PurchaseAnalyticsEvent?> HandleCheckoutCompleted(
        string eventId,
        string? clientReferenceId,
        long? amountTotal,
        string? stripeSessionId,
        string? stripeCustomerId,
        string? stripeSubscriptionId = null,
        string? stripePaymentIntentId = null,
        string? paymentStatus = "paid",
        string? currency = "usd",
        string? payload = null,
        bool requireProviderProof = false)
    {
        if (clientReferenceId is null)
        {
            _logger.LogError("[DEAD-LETTER] Checkout session completed but no ClientReferenceId — paid session cannot be fulfilled. StripeSessionId={SessionId}", stripeSessionId);
            throw new InvalidOperationException(
                "Paid checkout cannot be fulfilled because client_reference_id is missing.");
        }

        // BillingController sets clientReferenceId = "userId:subscription:tier".
        // Track-license purchasing has been removed, so subscription checkout is the
        // only fulfillment path remaining here.
        var parts = clientReferenceId.Split(':');
        if (parts.Length >= 3 && parts[1] == "subscription")
        {
            if (string.IsNullOrWhiteSpace(stripeSessionId))
                throw new InvalidOperationException(
                    "Subscription payment cannot be fulfilled because the Stripe session ID is missing.");
            return await HandleSubscriptionCheckout(
                eventId, parts[0], parts[2], stripeCustomerId, stripeSubscriptionId, stripeSessionId,
                amountTotal, paymentStatus, payload, requireProviderProof);
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
                throw new InvalidOperationException(
                    "Authorship payment cannot be fulfilled because the issuer is unavailable.");
            }

            if (string.IsNullOrWhiteSpace(stripeSessionId))
                throw new InvalidOperationException(
                    "Authorship payment cannot be fulfilled because the Stripe session ID is missing.");

            var expectedPrice = _config.GetValue<int?>("AuthorshipRecord:PriceCents") ?? 1000;
            if (requireProviderProof
                && (!string.Equals(paymentStatus, "paid", StringComparison.Ordinal)
                    || !string.Equals(currency, "usd", StringComparison.OrdinalIgnoreCase)
                    || amountTotal != expectedPrice))
            {
                throw new InvalidOperationException("Authorship payment does not match the server-controlled price or paid state.");
            }

            var record = await _db.AuthorshipRecords.FirstOrDefaultAsync(x => x.Id == recordId);
            if (record is null || !string.Equals(record.CreatorId, parts[0], StringComparison.Ordinal))
                throw new InvalidOperationException("Authorship payment owner does not match the pending record.");

            if (_db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"AuthorshipRecords\" WHERE \"Id\" = {recordId} FOR UPDATE");
                await _db.Entry(record).ReloadAsync();
            }
            record.PaymentStatus = "paid";
            record.StripePaymentIntentId ??= stripePaymentIntentId;

            await _authorshipIssuer.IssueForSessionAsync(recordId, stripeSessionId);
            return new PurchaseAnalyticsEvent
            {
                EventName = "authorship_record_purchased",
                StripeEventId = eventId,
                DistinctId = parts[0],
                Properties = new Dictionary<string, object?>
                {
                    ["record_id"] = recordId,
                    ["stripe_session_id"] = stripeSessionId,
                    ["amount_cents"] = amountTotal,
                    ["amount_source"] = "stripe_checkout_session_amount_total"
                }
            };
        }

        // ReleaseCreditService sets clientReferenceId = "userId:credits:N" (N = server-resolved
        // credit count). Payment grants N never-expiring purchased Release Ready credits.
        if (parts.Length == 3 && parts[1] == "credits" && int.TryParse(parts[2], out var grantCredits) && grantCredits > 0)
        {
            if (requireProviderProof
                && (!string.Equals(paymentStatus, "paid", StringComparison.Ordinal)
                    || !string.Equals(currency, "usd", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Release credit payment is not in a paid USD state.");

            return await GrantPurchasedCredits(eventId, parts[0], grantCredits, amountTotal,
                stripeSessionId, stripePaymentIntentId ?? ExtractPaymentIntentId(payload));
        }

        // Explicitly retired track-license checkout shape. No current endpoint can
        // create one, but historical Stripe event replays should be acknowledged
        // without recreating the removed licensing model.
        if (parts.Length == 3
            && Guid.TryParse(parts[1], out _)
            && parts[2] is "non-exclusive" or "exclusive" or "copyright-buyout")
        {
            _logger.LogWarning(
                "[IGNORED-RETIRED] Track-license checkout replay ignored. StripeSessionId={SessionId}",
                stripeSessionId);
            return null;
        }

        _logger.LogWarning(
            "[DEAD-LETTER] checkout.session.completed has an unrecognized clientReferenceId '{Ref}'. StripeSessionId={SessionId}",
            clientReferenceId, stripeSessionId);
        throw new InvalidOperationException(
            $"Paid checkout cannot be fulfilled because client_reference_id '{clientReferenceId}' is not recognized.");
    }

    /// <summary>
    /// Grant purchased Release Ready credits on a completed credit-pack checkout.
    /// Idempotent on the Stripe session id (a unique index also backs this) so webhook
    /// retries and duplicate deliveries never double-grant.
    /// </summary>
    private async Task<PurchaseAnalyticsEvent?> GrantPurchasedCredits(
        string eventId,
        string userId,
        int credits,
        long? amountTotal,
        string? stripeSessionId,
        string? stripePaymentIntentId)
    {
        if (string.IsNullOrWhiteSpace(stripeSessionId))
            throw new InvalidOperationException(
                "Release credit payment cannot be fulfilled because the Stripe session ID is missing.");

        var pack = CreditPackCatalog.FindByCredits(credits)
            ?? throw new InvalidOperationException(
                $"Release credit payment references unsupported credit count {credits}.");
        if (amountTotal != pack.PriceCents)
            throw new InvalidOperationException(
                $"Release credit payment amount mismatch for {credits} credits.");
        if (!await _db.Users.AnyAsync(u => u.Id == userId))
            throw new KeyNotFoundException(
                $"Release credit payment references unknown user {userId}.");

        if (!string.IsNullOrEmpty(stripeSessionId)
            && await _db.ReleaseCreditPurchases.AnyAsync(p => p.StripeSessionId == stripeSessionId))
        {
            return null; // already granted — idempotent no-op
        }

        _db.ReleaseCreditPurchases.Add(new Cambrian.Domain.Entities.ReleaseCreditPurchase
        {
            Id = Guid.NewGuid(),
            CreatorId = userId,
            Credits = credits,
            AmountCents = (int)(amountTotal ?? 0),
            Pack = pack.Id,
            Status = "paid",
            StripeSessionId = stripeSessionId,
            StripePaymentIntentId = stripePaymentIntentId,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "EVENT: ReleaseCreditsPurchased userId:{UserId} credits:{Credits} amountCents:{Amount} sessionId:{SessionId}",
            userId, credits, amountTotal ?? 0, stripeSessionId);
        Cambrian.Application.Observability.CambrianMetrics.CheckoutCompleted.Add(1);

        return new PurchaseAnalyticsEvent
        {
            EventName = "credit_pack_purchased",
            StripeEventId = eventId,
            DistinctId = userId,
            Properties = new Dictionary<string, object?>
            {
                ["pack"] = pack.Id,
                ["size"] = credits,
                ["amount_cents"] = amountTotal,
                ["amount_source"] = "stripe_checkout_session_amount_total",
                ["stripe_session_id"] = stripeSessionId
            }
        };
    }


    /// <summary>
    /// Handle a subscription checkout: create or update the user's Subscription record
    /// and upgrade their tier on the ApplicationUser.
    /// </summary>
    private async Task<PurchaseAnalyticsEvent?> HandleSubscriptionCheckout(
        string eventId,
        string userId,
        string tier,
        string? stripeCustomerId,
        string? stripeSubscriptionId = null,
        string? stripeSessionId = null,
        long? amountTotal = null,
        string? paymentStatus = "paid",
        string? payload = null,
        bool requireProviderProof = false)
    {
        if (tier is not ("creator" or "pro" or "paid"))
            throw new InvalidOperationException($"Subscription checkout references unsupported tier '{tier}'.");
        if (requireProviderProof
            && (string.IsNullOrWhiteSpace(stripeCustomerId) || string.IsNullOrWhiteSpace(stripeSubscriptionId)))
            throw new InvalidOperationException("Subscription checkout is missing its Stripe customer or subscription ID.");
        stripeCustomerId ??= $"cus_internal_{userId}";
        stripeSubscriptionId ??= $"sub_internal_{stripeSessionId}";

        // Normalize the tier slug to a known tier config (creator/pro).
        var tierConfig = TierManifest.For(tier);
        if (tier != "paid") tier = tierConfig.Slug;
        var stripeSnapshot = await ResolveCheckoutSubscriptionSnapshotAsync(stripeSubscriptionId, payload);

        if (requireProviderProof)
        {
            if (tier == "paid")
            {
                if (!string.Equals(paymentStatus, "paid", StringComparison.Ordinal) || amountTotal != 999)
                    throw new InvalidOperationException("Buyer subscription payment does not match the server-controlled amount.");
            }
            else
            {
                var expectedPriceId = tierConfig.StripePriceConfigKey is null
                    ? null
                    : _config[tierConfig.StripePriceConfigKey];
                if (string.IsNullOrWhiteSpace(expectedPriceId)
                    || !string.Equals(stripeSnapshot?.PriceId, expectedPriceId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Subscription checkout Price ID does not match the server-controlled tier mapping.");

                var acceptablePaymentState = string.Equals(paymentStatus, "paid", StringComparison.Ordinal)
                    || (string.Equals(paymentStatus, "no_payment_required", StringComparison.Ordinal)
                        && string.Equals(stripeSnapshot?.Status, "trialing", StringComparison.Ordinal));
                if (!acceptablePaymentState)
                    throw new InvalidOperationException("Subscription checkout is not paid and is not an authorized trial.");
            }
        }
        var localStatus = MapStripeStatusToSub(stripeSnapshot?.Status);
        if (localStatus is not "trialing")
        {
            localStatus = "active";
        }
        var trialEndsAt = stripeSnapshot?.TrialEndsAt;
        var expiresAt = stripeSnapshot?.PeriodEnd ?? trialEndsAt ?? DateTime.UtcNow.AddMonths(1);

        // Webhook idempotency: if this exact checkout session was already fulfilled,
        // do nothing. A duplicate/retried checkout.session.completed must not create a
        // second subscription or re-apply the tier. A unique filtered index on
        // StripeSessionId also enforces this at the DB level (mirrors GrantPurchasedCredits).
        if (!string.IsNullOrEmpty(stripeSessionId)
            && await _db.Subscriptions.AnyAsync(s => s.StripeSessionId == stripeSessionId))
        {
            return null; // already fulfilled — idempotent no-op
        }

        // Cancel any existing subscription for this user
        var existing = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId && (s.Status == "active" || s.Status == "trialing"));

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
            Status = localStatus,
            StripeCustomerId = stripeCustomerId,
            StripeSubscriptionId = stripeSubscriptionId,
            StripeSessionId = stripeSessionId,
            StartedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            TrialEndsAt = trialEndsAt
        };
        _db.Subscriptions.Add(subscription);

        // Update user tier from the tier manifest (free/creator/pro all map correctly).
        var user = await _db.Users.FindAsync(userId);
        if (user is not null)
        {
            user.Tier = tier;
            user.CreatorTier = tier == "paid" ? CreatorTier.Free : tierConfig.Tier;
            user.SubscriptionStatus = "Active";
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Subscription activated: User={UserId} Plan={Plan}",
            userId, tier);
        Cambrian.Application.Observability.CambrianMetrics.CheckoutCompleted.Add(1);

        return new PurchaseAnalyticsEvent
        {
            EventName = "subscription_started",
            StripeEventId = eventId,
            DistinctId = userId,
            Properties = new Dictionary<string, object?>
            {
                ["plan"] = tier,
                ["interval"] = "month",
                ["status"] = localStatus,
                ["trial_ends_at"] = trialEndsAt,
                ["stripe_customer_id"] = stripeCustomerId,
                ["stripe_subscription_id"] = stripeSubscriptionId,
                ["stripe_session_id"] = stripeSessionId
            }
        };
    }

    /// <summary>
    /// Handle invoice.paid — restore subscription health after a successful renewal payment.
    /// Uses the locally-stored StripeCustomerId when available so webhook processing
    /// does not depend on a live Stripe customer lookup.
    /// </summary>
    private async Task HandleInvoicePaid(
        string? stripeCustomerId,
        string? stripeSubscriptionId,
        string? invoiceId,
        DateTime? invoicePeriodEnd)
    {
        if (string.IsNullOrEmpty(stripeCustomerId)
            || string.IsNullOrEmpty(stripeSubscriptionId)
            || string.IsNullOrEmpty(invoiceId))
        {
            _logger.LogWarning("invoice.paid received without customer, subscription, or invoice ID");
            throw new InvalidOperationException(
                "invoice.paid cannot be fulfilled because required Stripe identifiers are missing.");
        }

        var user = await FindUserByStripeCustomerAsync(stripeCustomerId);
        if (user is null)
        {
            _logger.LogWarning(
                "invoice.paid: could not match Stripe customer {CustomerId} to a local user.",
                stripeCustomerId);
            throw new KeyNotFoundException(
                $"invoice.paid references unknown Stripe customer {stripeCustomerId}.");
        }

        var latestSubscription = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.UserId == user.Id && s.StripeSubscriptionId == stripeSubscriptionId);

        if (latestSubscription is null)
            throw new KeyNotFoundException($"invoice.paid references unknown subscription {stripeSubscriptionId}.");

        latestSubscription.Status = "active";
        latestSubscription.StripeCustomerId ??= stripeCustomerId;
        latestSubscription.LastStripeInvoiceId = invoiceId;
        latestSubscription.PaymentFailedAt = null;
        if (invoicePeriodEnd is not null) latestSubscription.ExpiresAt = invoicePeriodEnd;

        var tier = TierManifest.For(latestSubscription.Plan);
        user.Tier = latestSubscription.Plan;
        user.CreatorTier = latestSubscription.Plan == "paid" ? CreatorTier.Free : tier.Tier;

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
    private async Task<PurchaseAnalyticsEvent?> HandleSubscriptionDeleted(
        string eventId, string? stripeCustomerId, string? stripeSubscriptionId)
    {
        if (string.IsNullOrEmpty(stripeCustomerId) || string.IsNullOrEmpty(stripeSubscriptionId))
        {
            throw new InvalidOperationException("customer.subscription.deleted is missing customer or subscription ID.");
        }

        var user = await FindUserByStripeCustomerAsync(stripeCustomerId);
        if (user is null)
        {
            _logger.LogWarning(
                "customer.subscription.deleted: could not match Stripe customer {CustomerId} to a local user. Manual review needed.",
                stripeCustomerId);
            throw new KeyNotFoundException($"customer.subscription.deleted references unknown customer {stripeCustomerId}.");
        }

        var activeSub = await _db.Subscriptions.FirstOrDefaultAsync(s =>
            s.UserId == user.Id && s.StripeSubscriptionId == stripeSubscriptionId);
        activeSub ??= await _db.Subscriptions
            .Where(s => s.UserId == user.Id && s.StripeSubscriptionId == null
                && (s.Status == "active" || s.Status == "trialing" || s.Status == "past_due"))
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync();

        if (activeSub is null)
            throw new KeyNotFoundException($"customer.subscription.deleted references unknown subscription {stripeSubscriptionId}.");
        activeSub.StripeSubscriptionId ??= stripeSubscriptionId;

        activeSub.Status = "cancelled";
        activeSub.ExpiresAt = DateTime.UtcNow;

        var previousPlan = user.Tier;
        var replacement = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.UserId == user.Id && s.Id != activeSub.Id
                && (s.Status == "active" || s.Status == "trialing")
                && (s.ExpiresAt == null || s.ExpiresAt > DateTime.UtcNow))
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync();
        if (replacement is null)
        {
            user.Tier = "free";
            user.CreatorTier = CreatorTier.Free;
            user.SubscriptionStatus = "Cancelled";
        }
        else
        {
            user.Tier = replacement.Plan;
            user.CreatorTier = replacement.Plan == "paid" ? CreatorTier.Free : TierManifest.For(replacement.Plan).Tier;
            user.SubscriptionStatus = "Active";
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Subscription deleted via webhook: User={UserId} StripeCustomer={CustomerId} downgraded from {OldTier} to {NewTier}",
            user.Id, stripeCustomerId, previousPlan, user.Tier);

        return new PurchaseAnalyticsEvent
        {
            EventName = "subscription_canceled",
            StripeEventId = eventId,
            DistinctId = user.Id,
            Properties = new Dictionary<string, object?>
            {
                ["plan"] = previousPlan,
                ["stripe_customer_id"] = stripeCustomerId
            }
        };
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
        long? trialEndUnix = null;
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
                    trialEndUnix = ExtractTrialEnd(obj);
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
            throw new InvalidOperationException("customer.subscription.updated is missing its customer ID.");
        }

        var user = await FindUserByStripeCustomerAsync(customerId);
        if (user is null)
        {
            _logger.LogWarning(
                "customer.subscription.updated: could not match Stripe customer {CustomerId} to a local user.",
                customerId);
            throw new KeyNotFoundException($"customer.subscription.updated references unknown customer {customerId}.");
        }

        var sub = !string.IsNullOrWhiteSpace(stripeSubscriptionId)
            ? await _db.Subscriptions.FirstOrDefaultAsync(s =>
                s.UserId == user.Id && s.StripeSubscriptionId == stripeSubscriptionId)
            : null;
        sub ??= await _db.Subscriptions
            .Where(s => s.UserId == user.Id && s.StripeSubscriptionId == null)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync();
        if (sub is null)
            throw new KeyNotFoundException($"customer.subscription.updated references unknown subscription {stripeSubscriptionId}.");
        sub.StripeSubscriptionId ??= stripeSubscriptionId;

        var tierSlug = MapPriceToTierSlug(priceId);
        var grantsAccess = status is "active" or "trialing";
        if (grantsAccess && tierSlug is null)
            throw new InvalidOperationException($"Subscription {stripeSubscriptionId} uses an unrecognized Stripe Price ID.");
        var userStatus = MapStripeStatusToUser(status);
        var subStatus = MapStripeStatusToSub(status);
        if (subStatus == "unknown")
            throw new InvalidOperationException($"Subscription {stripeSubscriptionId} has unsupported Stripe status '{status}'.");
        var expiresAt = periodEndUnix is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(periodEndUnix.Value).UtcDateTime
            : (DateTime?)null;
        var trialEndsAt = trialEndUnix is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(trialEndUnix.Value).UtcDateTime
            : (DateTime?)null;
        var shouldDowngradeTrial = ShouldDowngradeFailedTrial(status, sub, trialEndsAt);

        sub.Status = subStatus;
        if (tierSlug is not null) sub.Plan = tierSlug;
        sub.StripeCustomerId ??= customerId;
        if (expiresAt is not null) sub.ExpiresAt = expiresAt;
        if (trialEndsAt is not null) sub.TrialEndsAt = trialEndsAt;

        if (!grantsAccess || shouldDowngradeTrial)
        {
            user.Tier = TierManifest.Free.Slug;
            user.CreatorTier = CreatorTier.Free;
            sub.ExpiresAt = DateTime.UtcNow;
        }
        else if (tierSlug is not null)
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

    private async Task<bool> RevokeSubscriptionForPaymentEventAsync(string? stripeCustomerId, string status)
    {
        if (string.IsNullOrWhiteSpace(stripeCustomerId)) return false;

        var subscription = await _db.Subscriptions
            .Where(s => s.StripeCustomerId == stripeCustomerId
                && (s.Status == "active" || s.Status == "trialing" || s.Status == "past_due"))
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync();
        if (subscription is null) return false;

        var now = DateTime.UtcNow;
        subscription.Status = status;
        subscription.ExpiresAt = now;
        if (status == "refunded") subscription.RefundedAt = now;
        if (status == "disputed") subscription.DisputedAt = now;

        var user = await _db.Users.FindAsync(subscription.UserId);
        if (user is not null)
        {
            user.Tier = TierManifest.Free.Slug;
            user.CreatorTier = CreatorTier.Free;
            user.SubscriptionStatus = status == "refunded" ? "Refunded" : "Disputed";
        }

        _logger.LogWarning(
            "EVENT: entitlement_changed action:subscription_{Status} userId:{UserId} stripeCustomerId:{CustomerId}",
            status, subscription.UserId, stripeCustomerId);
        Cambrian.Application.Observability.CambrianMetrics.EntitlementChanged.Add(1);
        return true;
    }

    /// <summary>
    /// Handle customer.subscription.trial_will_end — acknowledge Stripe's notice and
    /// keep the local trial end date fresh. User-facing reminder UX is intentionally
    /// handled outside the webhook; this event must not downgrade or charge anyone.
    /// </summary>
    private async Task HandleSubscriptionTrialWillEnd(string? stripeCustomerId, string? stripeSubscriptionId, string? payload)
    {
        var customerId = stripeCustomerId;
        var subscriptionId = stripeSubscriptionId;
        DateTime? trialEndsAt = null;

        if (!string.IsNullOrEmpty(payload))
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("object", out var obj))
                {
                    customerId ??= obj.TryGetProperty("customer", out var c) ? c.GetString() : null;
                    subscriptionId ??= obj.TryGetProperty("id", out var id) ? id.GetString() : null;
                    var trialEndUnix = ExtractTrialEnd(obj);
                    trialEndsAt = trialEndUnix is > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(trialEndUnix.Value).UtcDateTime
                        : null;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "customer.subscription.trial_will_end: failed to parse payload");
            }
        }

        if (string.IsNullOrEmpty(customerId))
        {
            _logger.LogWarning("customer.subscription.trial_will_end: no customer id resolved");
            return;
        }

        var user = await FindUserByStripeCustomerAsync(customerId);
        if (user is null)
        {
            _logger.LogWarning(
                "customer.subscription.trial_will_end: could not match Stripe customer {CustomerId} to a local user.",
                customerId);
            return;
        }

        var sub = await _db.Subscriptions
            .Where(s => s.UserId == user.Id)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync();

        if (sub is not null)
        {
            if (!string.IsNullOrEmpty(subscriptionId)) sub.StripeSubscriptionId ??= subscriptionId;
            sub.StripeCustomerId ??= customerId;
            if (trialEndsAt is not null) sub.TrialEndsAt = trialEndsAt;
            if (sub.Status == "active" && sub.TrialEndsAt is not null && sub.TrialEndsAt > DateTime.UtcNow)
            {
                sub.Status = "trialing";
            }
            await _db.SaveChangesAsync();
        }

        _logger.LogInformation(
            "Subscription trial will end: User={UserId} StripeCustomer={CustomerId} TrialEndsAt={TrialEndsAt}",
            user.Id, customerId, trialEndsAt);
    }

    /// <summary>Match a Stripe price id to a tier slug using configured price ids.</summary>
    private string? MapPriceToTierSlug(string? priceId)
    {
        if (string.IsNullOrWhiteSpace(priceId)) return null;
        if (priceId == _config["Stripe:Prices:Creator"]) return "creator";
        if (priceId == _config["Stripe:Prices:Pro"]) return "pro";
        return null;
    }

    private sealed record StripeSubscriptionSnapshot(
        string? Status,
        DateTime? TrialEndsAt,
        DateTime? PeriodEnd,
        string? PriceId);

    private async Task<StripeSubscriptionSnapshot?> ResolveCheckoutSubscriptionSnapshotAsync(string? stripeSubscriptionId, string? payload)
    {
        var snapshot = ExtractCheckoutSubscriptionSnapshot(payload);
        if (snapshot is not null)
            return snapshot;

        // Tests and dev fallback payloads do not configure Stripe. In production,
        // failing to read the subscription should retry the webhook instead of
        // silently losing trial_end.
        if (string.IsNullOrWhiteSpace(stripeSubscriptionId) || string.IsNullOrWhiteSpace(_config["Stripe:SecretKey"]))
            return null;

        var service = new global::Stripe.SubscriptionService();
        var subscription = await service.GetAsync(stripeSubscriptionId);
        return new StripeSubscriptionSnapshot(
            subscription.Status,
            subscription.TrialEnd,
            subscription.CurrentPeriodEnd == default ? null : subscription.CurrentPeriodEnd,
            subscription.Items?.Data?.FirstOrDefault()?.Price?.Id);
    }

    private static StripeSubscriptionSnapshot? ExtractCheckoutSubscriptionSnapshot(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("object", out var obj) ||
                !obj.TryGetProperty("subscription", out var subscription) ||
                subscription.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return ExtractSubscriptionSnapshot(subscription);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static StripeSubscriptionSnapshot ExtractSubscriptionSnapshot(JsonElement obj)
    {
        var status = obj.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;
        var trialEndUnix = ExtractTrialEnd(obj);
        var periodEndUnix = ExtractPeriodEnd(obj);
        var trialEndsAt = trialEndUnix is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(trialEndUnix.Value).UtcDateTime
            : (DateTime?)null;
        var periodEnd = periodEndUnix is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(periodEndUnix.Value).UtcDateTime
            : (DateTime?)null;
        return new StripeSubscriptionSnapshot(status, trialEndsAt, periodEnd, ExtractPriceId(obj));
    }

    private static string MapStripeStatusToUser(string? stripeStatus) => (stripeStatus ?? "").ToLowerInvariant() switch
    {
        "active" or "trialing" => "Active",
        "past_due" or "unpaid" or "incomplete" => "PastDue",
        "canceled" or "incomplete_expired" => "Cancelled",
        _ => "Unknown"
    };

    private static string MapStripeStatusToSub(string? stripeStatus) => (stripeStatus ?? "").ToLowerInvariant() switch
    {
        "trialing" => "trialing",
        "active" => "active",
        "past_due" or "unpaid" or "incomplete" => "past_due",
        "canceled" or "incomplete_expired" => "cancelled",
        _ => "unknown"
    };

    private static bool ShouldDowngradeFailedTrial(string? stripeStatus, Cambrian.Domain.Entities.Subscription? sub, DateTime? trialEndsAt)
    {
        var normalized = (stripeStatus ?? "").ToLowerInvariant();
        if (normalized is not ("past_due" or "unpaid" or "incomplete"))
            return false;

        return sub?.Status == "trialing"
            || sub?.TrialEndsAt is not null
            || trialEndsAt is not null;
    }

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

    private static long? ExtractTrialEnd(JsonElement obj)
    {
        return obj.TryGetProperty("trial_end", out var trialEnd) && trialEnd.ValueKind == JsonValueKind.Number
            ? trialEnd.GetInt64()
            : null;
    }

    private static string? ExtractPaymentIntentId(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var obj = document.RootElement.GetProperty("data").GetProperty("object");
            if (!obj.TryGetProperty("payment_intent", out var paymentIntent)) return null;
            if (paymentIntent.ValueKind == JsonValueKind.String) return paymentIntent.GetString();
            return paymentIntent.ValueKind == JsonValueKind.Object
                && paymentIntent.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Handle invoice.payment_failed — mark the user's subscription as at risk.
    /// Stripe will retry automatically; if all retries fail it sends customer.subscription.deleted.
    /// </summary>
    private async Task HandleInvoicePaymentFailed(
        string? stripeCustomerId, string? stripeSubscriptionId, string? invoiceId)
    {
        if (string.IsNullOrEmpty(stripeCustomerId) || string.IsNullOrEmpty(stripeSubscriptionId))
        {
            throw new InvalidOperationException("invoice.payment_failed is missing customer or subscription ID.");
        }

        var user = await FindUserByStripeCustomerAsync(stripeCustomerId);
        if (user is not null)
        {
            var subscription = await _db.Subscriptions.FirstOrDefaultAsync(s =>
                s.UserId == user.Id && s.StripeSubscriptionId == stripeSubscriptionId);
            if (subscription is null)
                throw new KeyNotFoundException($"invoice.payment_failed references unknown subscription {stripeSubscriptionId}.");

            subscription.Status = "past_due";
            subscription.PaymentFailedAt = DateTime.UtcNow;
            subscription.LastStripeInvoiceId = invoiceId;
            subscription.ExpiresAt = DateTime.UtcNow;
            user.Tier = TierManifest.Free.Slug;
            user.CreatorTier = CreatorTier.Free;
            user.SubscriptionStatus = "PastDue";
            await _db.SaveChangesAsync();
            _logger.LogWarning(
                "Invoice payment failed: User={UserId} StripeCustomer={CustomerId} marked as PastDue",
                user.Id, stripeCustomerId);
        }
        else
        {
            throw new KeyNotFoundException($"invoice.payment_failed references unknown Stripe customer {stripeCustomerId}.");
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
    private async Task HandleChargeRefunded(
        string? stripePaymentIntentId,
        string? stripeCustomerId,
        bool? chargeFullyRefunded,
        long? chargeAmountRefunded)
    {
        if (string.IsNullOrEmpty(stripePaymentIntentId))
        {
            _logger.LogWarning("charge.refunded received without payment_intent ID");
            throw new InvalidOperationException(
                "charge.refunded cannot be reconciled because the payment intent ID is missing.");
        }

        var creditPurchase = await _db.ReleaseCreditPurchases
            .FirstOrDefaultAsync(p => p.StripePaymentIntentId == stripePaymentIntentId);
        if (creditPurchase is not null)
        {
            if (creditPurchase.Status == "paid")
            {
                creditPurchase.Status = "refunded";
                creditPurchase.RefundedAmountCents = checked((int)Math.Min(
                    chargeAmountRefunded ?? creditPurchase.AmountCents, int.MaxValue));
                creditPurchase.RefundedAt = DateTime.UtcNow;
                _logger.LogWarning(
                    "EVENT: entitlement_changed action:credit_pack_refunded creatorId:{CreatorId} paymentIntentId:{PaymentIntentId}",
                    creditPurchase.CreatorId, stripePaymentIntentId);
                Cambrian.Application.Observability.CambrianMetrics.EntitlementChanged.Add(1);
            }
            return;
        }

        var authorship = await _db.AuthorshipRecords
            .FirstOrDefaultAsync(r => r.StripePaymentIntentId == stripePaymentIntentId);
        if (authorship is not null)
        {
            authorship.Status = "refunded";
            authorship.PaymentStatus = "refunded";
            authorship.RefundedAt = DateTime.UtcNow;
            return;
        }

        if (!string.IsNullOrWhiteSpace(stripeCustomerId)
            && await RevokeSubscriptionForPaymentEventAsync(stripeCustomerId, "refunded"))
            return;

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
            throw new KeyNotFoundException(
                $"charge.refunded cannot find a checkout session for payment intent {stripePaymentIntentId}.");
        }

        var purchase = await _db.Purchases.FirstOrDefaultAsync(p => p.StripeSessionId == session.Id);
        if (purchase is null)
        {
            var sessionAuthorship = await _db.AuthorshipRecords
                .FirstOrDefaultAsync(r => r.StripeSessionId == session.Id);
            if (sessionAuthorship is not null)
            {
                sessionAuthorship.StripePaymentIntentId ??= stripePaymentIntentId;
                sessionAuthorship.Status = "refunded";
                sessionAuthorship.PaymentStatus = "refunded";
                sessionAuthorship.RefundedAt = DateTime.UtcNow;
                return;
            }

            var sessionSubscription = await _db.Subscriptions
                .FirstOrDefaultAsync(s => s.StripeSessionId == session.Id);
            if (sessionSubscription is not null)
            {
                await RevokeSubscriptionForPaymentEventAsync(sessionSubscription.StripeCustomerId, "refunded");
                return;
            }
            // Not a tracked track Purchase — e.g. a subscription invoice or a
            // Release Ready credit-pack refund (those have no Purchase row to claw
            // back here). There is nothing to reconcile, so succeed instead of
            // throwing (a 400/exception makes Stripe retry the refund webhook forever).
            _logger.LogInformation(
                "charge.refunded: no track Purchase for session {SessionId} (likely a subscription or credit-pack refund) — nothing to claw back, treating as no-op.",
                session.Id);
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
    private async Task HandleChargeDisputeCreated(string? stripePaymentIntentId, string? stripeCustomerId)
    {
        if (string.IsNullOrEmpty(stripePaymentIntentId))
        {
            _logger.LogWarning("charge.dispute.created received without payment_intent ID");
            throw new InvalidOperationException(
                "charge.dispute.created cannot be reconciled because the payment intent ID is missing.");
        }

        var creditPurchase = await _db.ReleaseCreditPurchases
            .FirstOrDefaultAsync(p => p.StripePaymentIntentId == stripePaymentIntentId);
        if (creditPurchase is not null)
        {
            if (creditPurchase.Status == "paid")
            {
                creditPurchase.Status = "disputed";
                creditPurchase.DisputedAt = DateTime.UtcNow;
                _logger.LogWarning(
                    "EVENT: entitlement_changed action:credit_pack_disputed creatorId:{CreatorId} paymentIntentId:{PaymentIntentId}",
                    creditPurchase.CreatorId, stripePaymentIntentId);
                Cambrian.Application.Observability.CambrianMetrics.EntitlementChanged.Add(1);
            }
            return;
        }

        var authorship = await _db.AuthorshipRecords
            .FirstOrDefaultAsync(r => r.StripePaymentIntentId == stripePaymentIntentId);
        if (authorship is not null)
        {
            authorship.Status = "disputed";
            authorship.PaymentStatus = "disputed";
            authorship.DisputedAt = DateTime.UtcNow;
            return;
        }

        if (!string.IsNullOrWhiteSpace(stripeCustomerId)
            && await RevokeSubscriptionForPaymentEventAsync(stripeCustomerId, "disputed"))
            return;

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
            throw new KeyNotFoundException(
                $"charge.dispute.created cannot find a checkout session for payment intent {stripePaymentIntentId}.");
        }

        var purchase = await _db.Purchases.FirstOrDefaultAsync(p => p.StripeSessionId == session.Id);
        if (purchase is null)
        {
            var sessionAuthorship = await _db.AuthorshipRecords
                .FirstOrDefaultAsync(r => r.StripeSessionId == session.Id);
            if (sessionAuthorship is not null)
            {
                sessionAuthorship.StripePaymentIntentId ??= stripePaymentIntentId;
                sessionAuthorship.Status = "disputed";
                sessionAuthorship.PaymentStatus = "disputed";
                sessionAuthorship.DisputedAt = DateTime.UtcNow;
                return;
            }

            var sessionSubscription = await _db.Subscriptions
                .FirstOrDefaultAsync(s => s.StripeSessionId == session.Id);
            if (sessionSubscription is not null)
            {
                await RevokeSubscriptionForPaymentEventAsync(sessionSubscription.StripeCustomerId, "disputed");
                return;
            }
            // No tracked track Purchase (e.g. a subscription or credit-pack charge) —
            // nothing to reconcile here; succeed so Stripe does not retry the dispute
            // webhook indefinitely.
            _logger.LogInformation(
                "charge.dispute.created: no track Purchase for session {SessionId} (subscription/credit-pack) — no-op.",
                session.Id);
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
