using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;

namespace Cambrian.Infrastructure.Stripe;

/// <summary>
/// Stripe Connect webhook processing (money-in on artists' connected accounts).
///
/// <para>Mirrors <see cref="StripeWebhookService"/>'s integrity model: signature
/// verification is mandatory (separate <c>Stripe:ConnectWebhookSecret</c>), every
/// event is recorded in the <c>StripeWebhookEvents</c> ledger (EventId unique →
/// idempotent retries), and business effects run in a single DB transaction.</para>
///
/// <para>Earnings rules: tips write one ledger row on <c>checkout.session.completed</c>
/// (fee 0 at launch). Fan subscriptions write the first payment on
/// <c>checkout.session.completed</c> and renewals on <c>invoice.paid</c> with
/// <c>billing_reason=subscription_cycle</c> (skipping <c>subscription_create</c>
/// avoids double-counting the first period regardless of event ordering). The
/// 15% fee is recorded as <c>fee = gross − floor(gross × 0.85)</c> — net is always
/// floored, matching the platform-wide creator-credit invariant.</para>
/// </summary>
public class StripeConnectWebhookService : IConnectWebhookService
{
    public const decimal SubscriptionFeeRate = 0.15m;

    private readonly CambrianDbContext _db;
    private readonly string _webhookSecret;
    private readonly ILogger<StripeConnectWebhookService> _logger;

    public StripeConnectWebhookService(
        CambrianDbContext db,
        IConfiguration configuration,
        ILogger<StripeConnectWebhookService> logger)
    {
        _db = db;
        _webhookSecret = configuration["Stripe:ConnectWebhookSecret"] ?? "";
        _logger = logger;
    }

    public async Task HandleStripeAsync(string payload, string signature)
    {
        if (string.IsNullOrEmpty(_webhookSecret))
            throw new InvalidOperationException(
                "Stripe Connect webhook signature verification failed. "
                + "Stripe:ConnectWebhookSecret is not configured. Cannot process Connect webhooks without signature verification.");

        if (string.IsNullOrEmpty(signature))
            throw new InvalidOperationException(
                "Stripe Connect webhook signature verification failed. Stripe-Signature header is missing.");

        // Verify the exact request bytes first, then parse only the stable fields we use.
        // Stripe.net's typed EventConverter can fail on newer/minimal Connect payloads even
        // when the signature and JSON are valid; fulfillment must not depend on that converter.
        EventUtility.ValidateSignature(payload, signature, _webhookSecret);
        var signedContext = ParseSignedContext(payload);

        _logger.LogInformation(
            "Stripe Connect webhook verified: {EventType} {EventId} account:{Account}",
            signedContext.EventType, signedContext.EventId, signedContext.AccountId);

        await ProcessEventAsync(
            signedContext.EventId, signedContext.EventType, signedContext.AccountId, payload,
            signedContext.ClientReferenceId, signedContext.SessionId, signedContext.AmountTotal, signedContext.SubscriptionId,
            signedContext.InvoiceId, signedContext.SubscriptionId, signedContext.BillingReason, signedContext.AmountPaid,
            signedContext.SubscriptionId, signedContext.SubscriptionStatus,
            signedContext.PaymentStatus, signedContext.Currency);
    }

    private sealed record SignedConnectContext(
        string? EventId,
        string EventType,
        string? AccountId,
        string? ClientReferenceId,
        string? SessionId,
        string? SubscriptionId,
        string? InvoiceId,
        string? SubscriptionStatus,
        string? PaymentStatus,
        string? Currency,
        long? AmountTotal,
        long? AmountPaid,
        string? BillingReason);

    private static SignedConnectContext ParseSignedContext(string payload)
    {
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString() ?? string.Empty;
        var obj = root.GetProperty("data").GetProperty("object");

        string? ReadId(string name)
        {
            if (!obj.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind == System.Text.Json.JsonValueKind.String) return value.GetString();
            return value.ValueKind == System.Text.Json.JsonValueKind.Object
                && value.TryGetProperty("id", out var id) ? id.GetString() : null;
        }

        var clientRef = obj.TryGetProperty("client_reference_id", out var cr) ? cr.GetString() : null;
        var sessionId = type == "checkout.session.completed" ? ReadId("id") : null;
        var subscriptionId = ReadId("subscription");
        if (type.StartsWith("customer.subscription.", StringComparison.Ordinal))
            subscriptionId ??= ReadId("id");
        if (subscriptionId is null
            && obj.TryGetProperty("parent", out var parent)
            && parent.TryGetProperty("subscription_details", out var details)
            && details.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (details.TryGetProperty("subscription", out var subscription))
                subscriptionId = subscription.ValueKind == System.Text.Json.JsonValueKind.String
                    ? subscription.GetString()
                    : subscription.TryGetProperty("id", out var id) ? id.GetString() : null;
        }

        return new SignedConnectContext(
            root.TryGetProperty("id", out var eventId) ? eventId.GetString() : null,
            type,
            root.TryGetProperty("account", out var account) ? account.GetString() : null,
            clientRef,
            sessionId,
            subscriptionId,
            type.StartsWith("invoice.", StringComparison.Ordinal) ? ReadId("id") : null,
            obj.TryGetProperty("status", out var status) ? status.GetString() : null,
            obj.TryGetProperty("payment_status", out var paid) ? paid.GetString() : null,
            obj.TryGetProperty("currency", out var currency) ? currency.GetString() : null,
            obj.TryGetProperty("amount_total", out var amountTotal) && amountTotal.TryGetInt64(out var total) ? total : null,
            obj.TryGetProperty("amount_paid", out var amountPaid) && amountPaid.TryGetInt64(out var paidAmount) ? paidAmount : null,
            obj.TryGetProperty("billing_reason", out var billingReason) ? billingReason.GetString() : null);
    }

    /// <summary>
    /// Idempotent, transactional event processing. Public so tests can exercise the
    /// business logic without Stripe signatures (mirrors the platform webhook tests).
    /// </summary>
    public async Task ProcessEventAsync(
        string? eventId,
        string eventType,
        string? connectedAccountId,
        string? payload = null,
        string? clientReferenceId = null,
        string? sessionId = null,
        long? amountTotal = null,
        string? sessionSubscriptionId = null,
        string? invoiceId = null,
        string? invoiceSubscriptionId = null,
        string? billingReason = null,
        long? amountPaid = null,
        string? subscriptionId = null,
        string? subscriptionStatus = null,
        string? paymentStatus = "paid",
        string? currency = "usd")
    {
        if (string.IsNullOrWhiteSpace(eventId))
            throw new InvalidOperationException(
                "Stripe Connect webhook rejected: event ID is required for idempotency.");

        var normalizedEventId = eventId;

        // Idempotency: skip events already completed (shared ledger with the platform webhook).
        var existing = await _db.StripeWebhookEvents.FirstOrDefaultAsync(e => e.EventId == normalizedEventId);
        if (existing is not null && existing.Status == "completed")
        {
            _logger.LogInformation("Connect webhook event {EventId} already processed — skipping.", normalizedEventId);
            return;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            if (_db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({normalizedEventId}, 0))");
                var completed = await _db.StripeWebhookEvents.AsNoTracking()
                    .AnyAsync(e => e.EventId == normalizedEventId && e.Status == "completed");
                if (completed)
                {
                    await transaction.CommitAsync();
                    _logger.LogInformation("Connect webhook event {EventId} already completed after claim — skipping.", normalizedEventId);
                    return;
                }
                existing = await _db.StripeWebhookEvents.FirstOrDefaultAsync(e => e.EventId == normalizedEventId);
            }

            if (existing is null)
            {
                existing = new StripeWebhookEvent
                {
                    Id = Guid.NewGuid(),
                    EventId = normalizedEventId,
                    EventType = $"connect:{eventType}",
                    Status = "processing",
                    Payload = payload ?? "",
                    ReceivedAt = DateTime.UtcNow,
                };
                _db.StripeWebhookEvents.Add(existing);
            }
            else
            {
                existing.Status = "processing";
            }
            await _db.SaveChangesAsync();

            switch (eventType)
            {
                case "checkout.session.completed":
                    await HandleCheckoutCompletedAsync(
                        connectedAccountId, clientReferenceId, sessionId, amountTotal,
                        sessionSubscriptionId, paymentStatus, currency);
                    break;
                case "invoice.paid":
                    await HandleInvoicePaidAsync(
                        connectedAccountId, invoiceId, invoiceSubscriptionId, billingReason, amountPaid);
                    break;
                case "customer.subscription.deleted":
                    await HandleSubscriptionDeletedAsync(connectedAccountId, subscriptionId);
                    break;
                case "customer.subscription.updated":
                    await HandleSubscriptionUpdatedAsync(connectedAccountId, subscriptionId, subscriptionStatus);
                    break;
                case "invoice.payment_failed":
                    await HandleInvoicePaymentFailedAsync(connectedAccountId, invoiceId, invoiceSubscriptionId);
                    break;
                default:
                    _logger.LogInformation("Connect webhook event type {EventType} not handled.", eventType);
                    break;
            }

            existing.Status = "completed";
            existing.Processed = true;
            existing.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _db.ChangeTracker.Clear();

            if (IsUniqueViolation(ex))
            {
                _logger.LogInformation(
                    "Connect webhook event {EventId} lost a concurrent idempotency race — treating as duplicate.",
                    normalizedEventId);
                return;
            }

            try
            {
                var failed = await _db.StripeWebhookEvents.FirstOrDefaultAsync(e => e.EventId == normalizedEventId);
                if (failed is not null)
                {
                    failed.Status = "failed";
                    failed.ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                }
                else
                {
                    _db.StripeWebhookEvents.Add(new StripeWebhookEvent
                    {
                        Id = Guid.NewGuid(),
                        EventId = normalizedEventId,
                        EventType = $"connect:{eventType}",
                        Status = "failed",
                        Payload = payload ?? "",
                        ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message,
                        ReceivedAt = DateTime.UtcNow,
                    });
                }
                await _db.SaveChangesAsync();
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Failed to record Connect webhook failure for {EventId}", normalizedEventId);
            }

            throw; // 500 → Stripe retries; idempotency makes the retry safe
        }
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (string.Equals(
                    current.GetType().GetProperty("SqlState")?.GetValue(current) as string,
                    "23505",
                    StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // ── checkout.session.completed: tips + fan-sub activation/first payment ──
    private async Task HandleCheckoutCompletedAsync(
        string? connectedAccountId,
        string? clientReferenceId,
        string? sessionId,
        long? amountTotal,
        string? sessionSubscriptionId,
        string? paymentStatus,
        string? currency)
    {
        if (!string.Equals(paymentStatus, "paid", StringComparison.Ordinal)
            || !string.Equals(currency, "usd", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Connect checkout is not in a paid USD state.");

        if (string.IsNullOrWhiteSpace(clientReferenceId))
        {
            _logger.LogError(
                "[DEAD-LETTER] Connect checkout.session.completed without clientReferenceId. Session={SessionId}",
                sessionId);
            throw new InvalidOperationException(
                "Connect checkout cannot be fulfilled because client_reference_id is missing.");
        }

        var parts = clientReferenceId.Split(':');

        // "{payerUserId}:tip:{artistUserId}"
        if (parts.Length == 3 && parts[1] == "tip")
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new InvalidOperationException("Tip checkout is missing its Stripe session ID.");
            if (amountTotal is not long gross || gross <= 0)
            {
                _logger.LogError("[DEAD-LETTER] Tip session {SessionId} has no amount.", sessionId);
                throw new InvalidOperationException($"Tip session {sessionId} has no valid amount.");
            }

            await RequireConnectedAccountAsync(connectedAccountId, parts[2], $"tip session {sessionId}");
            await AddEarningsOnceAsync(
                artistUserId: parts[2],
                source: "tip",
                grossCents: gross,
                feeCents: 0, // launch: tips carry no platform fee
                externalRef: sessionId,
                payerUserId: parts[0]);
            return;
        }

        // "{payerUserId}:fansub:{fanSubId}"
        if (parts.Length == 3 && parts[1] == "fansub" && Guid.TryParse(parts[2], out var fanSubId))
        {
            var fanSub = await _db.FanSubscriptions.FirstOrDefaultAsync(s => s.Id == fanSubId);
            if (fanSub is null)
            {
                _logger.LogError("[DEAD-LETTER] Fan-sub session {SessionId} references unknown row {FanSubId}.", sessionId, fanSubId);
                throw new KeyNotFoundException(
                    $"Fan subscription checkout references unknown row {fanSubId}.");
            }
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new InvalidOperationException("Fan subscription checkout is missing its Stripe session ID.");
            if (string.IsNullOrWhiteSpace(sessionSubscriptionId))
                throw new InvalidOperationException(
                    "Fan subscription checkout is missing its Stripe subscription ID.");
            if (amountTotal is not long gross || gross != fanSub.PriceCents)
                throw new InvalidOperationException(
                    $"Fan subscription amount does not match the configured price for {fanSubId}.");

            await RequireConnectedAccountAsync(
                connectedAccountId, fanSub.ArtistUserId, $"fan subscription session {sessionId}");

            if (fanSub.Status != "active")
            {
                fanSub.Status = "active";
                fanSub.ActivatedAt = DateTime.UtcNow;
                fanSub.StripeSessionId = sessionId;
                fanSub.StripeSubscriptionId = sessionSubscriptionId ?? fanSub.StripeSubscriptionId;
                await _db.SaveChangesAsync();
            }

            // First-period earnings keyed by session id; renewals come via invoice.paid.
            await AddEarningsOnceAsync(
                artistUserId: fanSub.ArtistUserId,
                source: "sub",
                grossCents: gross,
                feeCents: SubscriptionFee(gross),
                externalRef: sessionId,
                payerUserId: fanSub.FanUserId);
            return;
        }

        _logger.LogWarning(
            "[DEAD-LETTER] Connect checkout.session.completed with unrecognized clientReferenceId '{Ref}'. Session={SessionId}",
            clientReferenceId, sessionId);
        throw new InvalidOperationException(
            $"Connect checkout cannot be fulfilled because client_reference_id '{clientReferenceId}' is not recognized.");
    }

    // ── invoice.paid: fan-sub renewals ──
    private async Task HandleInvoicePaidAsync(
        string? connectedAccountId,
        string? invoiceId,
        string? invoiceSubscriptionId,
        string? billingReason,
        long? amountPaid)
    {
        // The first period is recorded from checkout.session.completed — only cycles here.
        if (!string.Equals(billingReason, "subscription_cycle", StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Connect invoice.paid with billing_reason '{Reason}' skipped (only subscription_cycle is recorded).",
                billingReason);
            return;
        }

        if (string.IsNullOrWhiteSpace(invoiceId)
            || string.IsNullOrWhiteSpace(invoiceSubscriptionId)
            || amountPaid is not long gross
            || gross <= 0)
        {
            _logger.LogError("[DEAD-LETTER] Connect invoice.paid missing invoice, subscription, or amount. Invoice={InvoiceId}", invoiceId);
            throw new InvalidOperationException(
                "Connect invoice.paid is missing required fulfillment identifiers or amount.");
        }

        var fanSub = await _db.FanSubscriptions
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == invoiceSubscriptionId);
        if (fanSub is null)
        {
            _logger.LogError(
                "[DEAD-LETTER] Connect invoice.paid for unknown subscription {SubscriptionId}. Invoice={InvoiceId}",
                invoiceSubscriptionId, invoiceId);
            throw new KeyNotFoundException(
                $"Connect invoice.paid references unknown subscription {invoiceSubscriptionId}.");
        }

        await RequireConnectedAccountAsync(
            connectedAccountId, fanSub.ArtistUserId, $"invoice {invoiceId}");
        fanSub.Status = "active";
        fanSub.PaymentFailedAt = null;
        await AddEarningsOnceAsync(
            artistUserId: fanSub.ArtistUserId,
            source: "sub",
            grossCents: gross,
            feeCents: SubscriptionFee(gross),
            externalRef: invoiceId,
            payerUserId: fanSub.FanUserId);
    }

    // ── customer.subscription.deleted: fan-sub cancellation ──
    private async Task HandleSubscriptionDeletedAsync(
        string? connectedAccountId, string? stripeSubscriptionId)
    {
        if (string.IsNullOrWhiteSpace(stripeSubscriptionId))
            throw new InvalidOperationException(
                "Connect subscription deletion is missing its Stripe subscription ID.");

        var fanSub = await _db.FanSubscriptions
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId);
        if (fanSub is null)
        {
            _logger.LogWarning(
                "[DEAD-LETTER] Connect subscription.deleted for unknown subscription {SubscriptionId}.",
                stripeSubscriptionId);
            throw new KeyNotFoundException(
                $"Connect subscription deletion references unknown subscription {stripeSubscriptionId}.");
        }

        await RequireConnectedAccountAsync(
            connectedAccountId, fanSub.ArtistUserId, $"subscription {stripeSubscriptionId}");
        if (fanSub.Status != "cancelled")
        {
            fanSub.Status = "cancelled";
            fanSub.CancelledAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "EVENT: FanSubscriptionCancelled fanSubId:{FanSubId} artistId:{ArtistId}",
                fanSub.Id, fanSub.ArtistUserId);
        }
    }

    private async Task HandleSubscriptionUpdatedAsync(
        string? connectedAccountId, string? stripeSubscriptionId, string? stripeStatus)
    {
        if (string.IsNullOrWhiteSpace(stripeSubscriptionId) || string.IsNullOrWhiteSpace(stripeStatus))
            throw new InvalidOperationException("Connect subscription update is missing subscription ID or status.");

        var fanSub = await _db.FanSubscriptions
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId)
            ?? throw new KeyNotFoundException($"Connect subscription update references unknown subscription {stripeSubscriptionId}.");
        await RequireConnectedAccountAsync(connectedAccountId, fanSub.ArtistUserId, $"subscription {stripeSubscriptionId}");

        fanSub.Status = stripeStatus switch
        {
            "active" or "trialing" => "active",
            "past_due" or "unpaid" or "incomplete" => "past_due",
            "canceled" or "incomplete_expired" => "cancelled",
            _ => throw new InvalidOperationException($"Unsupported Connect subscription status '{stripeStatus}'."),
        };
        if (fanSub.Status == "past_due") fanSub.PaymentFailedAt = DateTime.UtcNow;
        if (fanSub.Status == "active") fanSub.PaymentFailedAt = null;
        if (fanSub.Status == "cancelled") fanSub.CancelledAt ??= DateTime.UtcNow;
    }

    private async Task HandleInvoicePaymentFailedAsync(
        string? connectedAccountId, string? invoiceId, string? stripeSubscriptionId)
    {
        if (string.IsNullOrWhiteSpace(invoiceId) || string.IsNullOrWhiteSpace(stripeSubscriptionId))
            throw new InvalidOperationException("Connect invoice.payment_failed is missing invoice or subscription ID.");

        var fanSub = await _db.FanSubscriptions
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId)
            ?? throw new KeyNotFoundException($"Connect invoice.payment_failed references unknown subscription {stripeSubscriptionId}.");
        await RequireConnectedAccountAsync(connectedAccountId, fanSub.ArtistUserId, $"invoice {invoiceId}");
        fanSub.Status = "past_due";
        fanSub.PaymentFailedAt = DateTime.UtcNow;
    }

    // ── Append-only earnings write with (source, externalRef) idempotency ──
    private async Task AddEarningsOnceAsync(
        string artistUserId, string source, long grossCents, long feeCents, string externalRef, string? payerUserId)
    {
        var exists = await _db.EarningsTransactions
            .AnyAsync(t => t.Source == source && t.ExternalRef == externalRef);
        if (exists)
        {
            _logger.LogInformation(
                "Earnings row for {Source}/{ExternalRef} already exists — skipping (idempotent).",
                source, externalRef);
            return;
        }

        _db.EarningsTransactions.Add(new EarningsTransaction
        {
            Id = Guid.NewGuid(),
            ArtistUserId = artistUserId,
            Source = source,
            GrossCents = grossCents,
            FeeCents = feeCents,
            NetCents = grossCents - feeCents,
            Currency = "usd",
            ExternalRef = externalRef,
            PayerUserId = string.IsNullOrWhiteSpace(payerUserId) || payerUserId == "anon" ? null : payerUserId,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "EVENT: EarningsRecorded artistId:{ArtistId} source:{Source} grossCents:{Gross} feeCents:{Fee} ref:{Ref}",
            artistUserId, source, grossCents, feeCents, externalRef);
    }

    private async Task RequireConnectedAccountAsync(
        string? eventAccountId, string artistUserId, string context)
    {
        if (string.IsNullOrWhiteSpace(eventAccountId))
            throw new InvalidOperationException(
                $"Stripe Connect account is missing for {context}.");

        var artist = await _db.Users.FindAsync(artistUserId)
            ?? throw new KeyNotFoundException(
                $"Artist {artistUserId} referenced by {context} does not exist.");
        if (!string.Equals(artist.StripeAccountId, eventAccountId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Stripe Connect account mismatch for {context}.");
    }

    /// <summary>Net is floored (platform invariant: never round the artist's share up).</summary>
    private static long SubscriptionFee(long grossCents)
    {
        var net = (long)Math.Floor(grossCents * (1 - SubscriptionFeeRate));
        return grossCents - net;
    }
}
