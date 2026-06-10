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

        var stripeEvent = EventUtility.ConstructEvent(payload, signature, _webhookSecret);

        string? clientReferenceId = null, sessionId = null, sessionSubscriptionId = null;
        long? amountTotal = null;
        string? invoiceId = null, invoiceSubscriptionId = null, billingReason = null;
        long? amountPaid = null;
        string? subscriptionId = null;

        switch (stripeEvent.Type)
        {
            case "checkout.session.completed":
                var session = stripeEvent.Data.Object as Session;
                clientReferenceId = session?.ClientReferenceId;
                sessionId = session?.Id;
                amountTotal = session?.AmountTotal;
                sessionSubscriptionId = session?.SubscriptionId;
                break;
            case "invoice.paid":
                var invoice = stripeEvent.Data.Object as global::Stripe.Invoice;
                invoiceId = invoice?.Id;
                invoiceSubscriptionId = invoice?.SubscriptionId;
                billingReason = invoice?.BillingReason;
                amountPaid = invoice?.AmountPaid;
                break;
            case "customer.subscription.deleted":
                var sub = stripeEvent.Data.Object as global::Stripe.Subscription;
                subscriptionId = sub?.Id;
                break;
        }

        _logger.LogInformation(
            "Stripe Connect webhook verified: {EventType} {EventId} account:{Account}",
            stripeEvent.Type, stripeEvent.Id, stripeEvent.Account);

        await ProcessEventAsync(
            stripeEvent.Id, stripeEvent.Type, stripeEvent.Account, payload,
            clientReferenceId, sessionId, amountTotal, sessionSubscriptionId,
            invoiceId, invoiceSubscriptionId, billingReason, amountPaid,
            subscriptionId);
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
        string? subscriptionId = null)
    {
        var normalizedEventId = string.IsNullOrWhiteSpace(eventId) ? $"connect-{Guid.NewGuid():N}" : eventId!;

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
                    await HandleCheckoutCompletedAsync(clientReferenceId, sessionId, amountTotal, sessionSubscriptionId);
                    break;
                case "invoice.paid":
                    await HandleInvoicePaidAsync(invoiceId, invoiceSubscriptionId, billingReason, amountPaid);
                    break;
                case "customer.subscription.deleted":
                    await HandleSubscriptionDeletedAsync(subscriptionId);
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

    // ── checkout.session.completed: tips + fan-sub activation/first payment ──
    private async Task HandleCheckoutCompletedAsync(
        string? clientReferenceId, string? sessionId, long? amountTotal, string? sessionSubscriptionId)
    {
        if (string.IsNullOrWhiteSpace(clientReferenceId))
        {
            _logger.LogWarning(
                "[IGNORED] Connect checkout.session.completed without clientReferenceId. Session={SessionId}",
                sessionId);
            return;
        }

        var parts = clientReferenceId.Split(':');

        // "{payerUserId}:tip:{artistUserId}"
        if (parts.Length == 3 && parts[1] == "tip")
        {
            if (amountTotal is not long gross || gross <= 0)
            {
                _logger.LogError("[DEAD-LETTER] Tip session {SessionId} has no amount.", sessionId);
                return;
            }

            await AddEarningsOnceAsync(
                artistUserId: parts[2],
                source: "tip",
                grossCents: gross,
                feeCents: 0, // launch: tips carry no platform fee
                externalRef: sessionId ?? clientReferenceId,
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
                return;
            }

            if (fanSub.Status != "active")
            {
                fanSub.Status = "active";
                fanSub.ActivatedAt = DateTime.UtcNow;
                fanSub.StripeSessionId = sessionId;
                fanSub.StripeSubscriptionId = sessionSubscriptionId ?? fanSub.StripeSubscriptionId;
                await _db.SaveChangesAsync();
            }

            // First-period earnings keyed by session id; renewals come via invoice.paid.
            var gross = amountTotal ?? fanSub.PriceCents;
            await AddEarningsOnceAsync(
                artistUserId: fanSub.ArtistUserId,
                source: "sub",
                grossCents: gross,
                feeCents: SubscriptionFee(gross),
                externalRef: sessionId ?? clientReferenceId,
                payerUserId: fanSub.FanUserId);
            return;
        }

        _logger.LogWarning(
            "[IGNORED] Connect checkout.session.completed with unrecognized clientReferenceId '{Ref}'. Session={SessionId}",
            clientReferenceId, sessionId);
    }

    // ── invoice.paid: fan-sub renewals ──
    private async Task HandleInvoicePaidAsync(
        string? invoiceId, string? invoiceSubscriptionId, string? billingReason, long? amountPaid)
    {
        // The first period is recorded from checkout.session.completed — only cycles here.
        if (!string.Equals(billingReason, "subscription_cycle", StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Connect invoice.paid with billing_reason '{Reason}' skipped (only subscription_cycle is recorded).",
                billingReason);
            return;
        }

        if (string.IsNullOrWhiteSpace(invoiceSubscriptionId) || amountPaid is not long gross || gross <= 0)
        {
            _logger.LogWarning("[IGNORED] Connect invoice.paid missing subscription or amount. Invoice={InvoiceId}", invoiceId);
            return;
        }

        var fanSub = await _db.FanSubscriptions
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == invoiceSubscriptionId);
        if (fanSub is null)
        {
            _logger.LogError(
                "[DEAD-LETTER] Connect invoice.paid for unknown subscription {SubscriptionId}. Invoice={InvoiceId}",
                invoiceSubscriptionId, invoiceId);
            return;
        }

        await AddEarningsOnceAsync(
            artistUserId: fanSub.ArtistUserId,
            source: "sub",
            grossCents: gross,
            feeCents: SubscriptionFee(gross),
            externalRef: invoiceId ?? $"sub-cycle-{invoiceSubscriptionId}-{DateTime.UtcNow:yyyyMM}",
            payerUserId: fanSub.FanUserId);
    }

    // ── customer.subscription.deleted: fan-sub cancellation ──
    private async Task HandleSubscriptionDeletedAsync(string? stripeSubscriptionId)
    {
        if (string.IsNullOrWhiteSpace(stripeSubscriptionId))
            return;

        var fanSub = await _db.FanSubscriptions
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId);
        if (fanSub is null)
        {
            _logger.LogWarning(
                "[IGNORED] Connect subscription.deleted for unknown subscription {SubscriptionId}.",
                stripeSubscriptionId);
            return;
        }

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

    /// <summary>Net is floored (platform invariant: never round the artist's share up).</summary>
    private static long SubscriptionFee(long grossCents)
    {
        var net = (long)Math.Floor(grossCents * (1 - SubscriptionFeeRate));
        return grossCents - net;
    }
}
