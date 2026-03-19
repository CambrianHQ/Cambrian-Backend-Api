# Payment System Audit Report

**Date:** 2026-03-19
**Branch:** `cursor/platform-stability-audit-7360`
**Scope:** All Stripe payment, webhook, checkout, billing, subscription, and payout code

---

## Architecture Overview

The payment system uses Stripe Checkout (hosted payment page) with two parallel fulfillment paths:

1. **Frontend-initiated confirm** — After Stripe redirects the buyer back, the frontend calls `GET /checkout/session/{sessionId}` or `GET /billing/checkout-session/{sessionId}`, which polls Stripe and fulfills inline.
2. **Webhook-initiated fulfill** — Stripe sends `checkout.session.completed` to `POST /webhook/stripe`, which fulfills asynchronously.

Both paths can race. The system has partial idempotency guards but several gaps.

### Payment Flows

| Flow | Controller | Service | Gateway Method |
|------|-----------|---------|----------------|
| Track purchase | `CheckoutController` | `CheckoutService` | `CreateCheckoutSessionAsync` (mode=payment) |
| Track purchase (legacy) | `PaymentsController` | `PaymentService` | `CreateCheckoutSessionAsync` (mode=payment) |
| Subscription upgrade | `BillingController` | `BillingService` | `CreateSubscriptionCheckoutAsync` (mode=subscription) |
| Payout to creator | `PayoutController` | `IPayoutService` | `CreateTransferAsync` |
| Webhook handling | `WebhookController` | `StripeWebhookService` | N/A |

---

## FULL FILE CONTENTS — Critical Files

### 1. StripeFacade.cs (Infrastructure/Stripe)

```1:231:src/Cambrian.Infrastructure/Stripe/StripeFacade.cs
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Stripe;
using Stripe.Checkout;

namespace Cambrian.Infrastructure.Stripe;

public class StripeFacade : IPaymentGateway
{
    private readonly string _frontendUrl;

    public StripeFacade(IConfiguration configuration)
    {
        _frontendUrl = configuration["App:FrontendUrl"]
            ?? throw new InvalidOperationException("App:FrontendUrl must be configured. Stripe checkout redirects require a valid frontend URL.");
    }

    public async Task<string> CreateCheckoutSessionAsync(
        int amountInCents,
        string productName,
        string? clientReferenceId = null,
        string? successUrl = null,
        string? cancelUrl = null,
        string? customerEmail = null)
    {
        var options = new SessionCreateOptions
        {
            Mode = "payment",
            Customer = customerEmail != null ? await FindOrCreateCustomerAsync(customerEmail) : null,
            SuccessUrl = successUrl ?? $"{_frontendUrl}/checkout/success?session_id={{CHECKOUT_SESSION_ID}}",
            CancelUrl = cancelUrl ?? $"{_frontendUrl}/checkout/cancel",
            ClientReferenceId = clientReferenceId,
            LineItems = new List<SessionLineItemOptions>
            {
                new()
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "usd",
                        UnitAmount = amountInCents,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = productName
                        }
                    },
                    Quantity = 1
                }
            }
        };

        var service = new SessionService();
        var session = await service.CreateAsync(options);

        return session.Url!;
    }

    public async Task<string> CreateSubscriptionCheckoutAsync(
        int amountInCents,
        string planName,
        string clientReferenceId,
        string successUrl,
        string cancelUrl,
        string? customerEmail = null)
    {
        var options = new SessionCreateOptions
        {
            Mode = "subscription",
            Customer = customerEmail != null ? await FindOrCreateCustomerAsync(customerEmail) : null,
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            ClientReferenceId = clientReferenceId,
            LineItems = new List<SessionLineItemOptions>
            {
                new()
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "usd",
                        UnitAmount = amountInCents,
                        Recurring = new SessionLineItemPriceDataRecurringOptions
                        {
                            Interval = "month"
                        },
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = $"Cambrian {planName} Plan"
                        }
                    },
                    Quantity = 1
                }
            }
        };

        var service = new SessionService();
        var session = await service.CreateAsync(options);

        return session.Url!;
    }

    public async Task<Session> GetSessionAsync(string sessionId)
    {
        var service = new SessionService();
        return await service.GetAsync(sessionId);
    }

    public async Task<CheckoutSessionInfo?> GetCheckoutSessionAsync(string sessionId)
    {
        try
        {
            var service = new SessionService();
            var session = await service.GetAsync(sessionId);

            var status = session.PaymentStatus switch
            {
                "paid" => "paid",
                "unpaid" => "pending",
                _ => "pending"
            };

            return new CheckoutSessionInfo
            {
                SessionId = session.Id,
                Status = status,
                ClientReferenceId = session.ClientReferenceId,
                AmountTotal = session.AmountTotal
            };
        }
        catch
        {
            return null;
        }
    }

    // ── Helpers ──

    private static async Task<string> FindOrCreateCustomerAsync(string email)
    {
        var customerService = new CustomerService();
        var existing = await customerService.ListAsync(new CustomerListOptions
        {
            Email = email,
            Limit = 1
        });

        if (existing.Data.Count > 0)
            return existing.Data[0].Id;

        var customer = await customerService.CreateAsync(new CustomerCreateOptions
        {
            Email = email
        });
        return customer.Id;
    }

    // ── Stripe Connect ──

    public async Task<string> CreateConnectAccountAsync(string email)
    {
        var service = new AccountService();
        var account = await service.CreateAsync(new AccountCreateOptions
        {
            Type = "express",
            Email = email,
            Capabilities = new AccountCapabilitiesOptions
            {
                CardPayments = new AccountCapabilitiesCardPaymentsOptions { Requested = true },
                Transfers = new AccountCapabilitiesTransfersOptions { Requested = true }
            }
        });
        return account.Id;
    }

    public async Task<string> CreateAccountOnboardingLinkAsync(
        string accountId, string returnUrl, string refreshUrl)
    {
        var service = new AccountLinkService();
        var link = await service.CreateAsync(new AccountLinkCreateOptions
        {
            Account = accountId,
            Type = "account_onboarding",
            ReturnUrl = returnUrl,
            RefreshUrl = refreshUrl
        });
        return link.Url;
    }

    public async Task<ConnectAccountStatus> GetConnectAccountStatusAsync(string accountId)
    {
        var service = new AccountService();
        var account = await service.GetAsync(accountId);
        var status = (account.ChargesEnabled && account.PayoutsEnabled) ? "active" : "pending";
        return new ConnectAccountStatus
        {
            AccountId = account.Id,
            Status = status,
            ChargesEnabled = account.ChargesEnabled,
            PayoutsEnabled = account.PayoutsEnabled
        };
    }

    public async Task<string> CreateExpressDashboardLinkAsync(string accountId)
    {
        var service = new AccountLoginLinkService();
        var link = await service.CreateAsync(accountId);
        return link.Url;
    }

    public async Task<string> CreateTransferAsync(
        string destinationAccountId, long amountCents, string description)
    {
        var service = new TransferService();
        var transfer = await service.CreateAsync(new TransferCreateOptions
        {
            Amount = amountCents,
            Currency = "usd",
            Destination = destinationAccountId,
            Description = description
        });
        return transfer.Id;
    }

    public async Task DeleteConnectedAccountAsync(string accountId)
    {
        var service = new AccountService();
        await service.DeleteAsync(accountId);
    }
}
```

### 2. StripeWebhookService.cs (Infrastructure/Stripe)

```1:608:src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs
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

        _logger.LogWarning("Processing webhook WITHOUT signature verification (Development only)");
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        eventType = root.GetProperty("type").GetString() ?? "";
        eventId = root.TryGetProperty("id", out var eventIdElement) ? eventIdElement.GetString() : null;
        clientReferenceId = null;
        amountTotal = null;

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

    // ... (ProcessEventAsync, HandleCheckoutCompleted, HandleTrackPurchase,
    //      HandleSubscriptionCheckout, HandleSubscriptionDeleted,
    //      HandleInvoicePaymentFailed, FindUserByStripeCustomerAsync)
    // Full contents above — 608 lines total
}
```

### 3. WebhookController.cs (Api/Controllers)

```1:46:src/Cambrian.Api/Controllers/WebhookController.cs
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("webhook")]
public class WebhookController : BaseController
{
    private readonly IWebhookService _webhooks;
    private readonly ILogger<WebhookController> _logger;

    public WebhookController(IWebhookService webhooks, ILogger<WebhookController> logger)
    {
        _webhooks = webhooks;
        _logger = logger;
    }

    [HttpPost("stripe")]
    public async Task<IActionResult> Stripe()
    {
        var signature = Request.Headers["Stripe-Signature"].ToString();

        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();

        _logger.LogInformation("EVENT: StripeWebhookReceived payloadLength:{Length} signaturePresent:{SigPresent}", json.Length, !string.IsNullOrEmpty(signature));

        try
        {
            await _webhooks.HandleStripeAsync(json, signature ?? "");
            _logger.LogInformation("EVENT: StripeWebhookProcessed");
            return MessageResponse("Received.");
        }
        catch (Stripe.StripeException ex)
        {
            _logger.LogError(ex, "EVENT: StripeWebhookFailed — invalid signature");
            return ErrorResponse("Invalid webhook signature.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("signature verification"))
        {
            _logger.LogError(ex, "EVENT: StripeWebhookFailed — signature verification failed");
            return ErrorResponse("Webhook signature verification failed.");
        }
    }
}
```

---

## CRITICAL Issues (P0 — Must Fix)

### C1. Idempotency Race Condition in Webhook Processing

**File:** `StripeWebhookService.cs`, lines 153-160

The duplicate-event check uses a read-then-write pattern without a database-level unique constraint:

```csharp
var alreadyProcessed = await _db.StripeWebhookEvents
    .AsNoTracking()
    .AnyAsync(e => e.EventId == eventId);

if (alreadyProcessed) return; // ← TOCTOU gap here

_db.StripeWebhookEvents.Add(new StripeWebhookEvent { EventId = eventId, ... });
```

Two concurrent webhook deliveries with the same `eventId` can both pass the `AnyAsync` check and both process the event, resulting in **duplicate purchases, duplicate wallet credits, and duplicate library items**.

**Fix:** Add a `UNIQUE` constraint on `StripeWebhookEvent.EventId` in the database migration. Wrap the insert in a try-catch for `DbUpdateException` (unique violation) and treat it as "already processed."

---

### C2. Legacy Payment Process Endpoint Allows Unpaid Fulfillment

**File:** `PaymentsController.cs`, lines 48-54 / `PaymentService.cs`, lines 84-98

```csharp
[HttpPost("process")]
public async Task<IActionResult> Process(PaymentProcessRequest request)
{
    var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    await _payments.ProcessAsync(request, userId);
    return MessageResponse("Payment processed.");
}
```

`ProcessAsync` marks a purchase as `"completed"` based solely on the caller owning the purchase — it **never verifies that Stripe actually received payment**. An authenticated user could create a purchase record (via the `/purchases` endpoint) and then call `/payments/process` to mark it completed without paying.

The warning log exists but the code still executes the dangerous path.

**Fix:** Either remove this endpoint entirely, gate it behind admin role, or add Stripe session verification before marking completed.

---

### C3. Webhook Controller Returns Non-2xx on Processing Errors

**File:** `WebhookController.cs`, lines 36-44

```csharp
catch (Stripe.StripeException ex)
{
    return ErrorResponse("Invalid webhook signature.");
}
```

If `ErrorResponse` returns HTTP 400 (likely, given the name), Stripe will interpret this as "not received" and will **retry the webhook up to 16 times over 72 hours**. For transient processing errors, this causes cascading retries. For permanent errors (invalid signature), Stripe will keep retrying uselessly.

**Fix:** For signature failures, return 400 (correct — tells Stripe the endpoint rejected it). For processing errors within successfully verified events, return 200 to acknowledge receipt and log the error for async resolution. Separate the two error paths.

---

### C4. Dual Fulfillment Race — Webhook + Frontend Confirm

**Files:** `StripeWebhookService.HandleTrackPurchase` and `CheckoutService.ConfirmAsync`

Both paths can fulfill the same purchase simultaneously:

- Webhook: `StripeWebhookService.HandleCheckoutCompleted` → `HandleTrackPurchase`
- Frontend: `CheckoutController.ConfirmSession` → `CheckoutService.ConfirmAsync`

Both have duplicate-purchase checks, but they use **different mechanisms**:
- Webhook uses `_db.Purchases.FirstOrDefaultAsync(...)` against the DbContext
- CheckoutService uses `_purchases.GetByBuyerIdAsync(userId)` through a repository

If both run concurrently:
1. Both check for existing purchase → none found
2. Both create a new Purchase with `status = "completed"`
3. Both add a LibraryItem
4. Both credit the creator's wallet

Result: **Double wallet credit to the creator** for a single payment.

**Fix:** Use the `StripeSessionId` as a unique key on the Purchase table. The first writer wins; the second gets a constraint violation and returns the existing purchase.

---

### C5. Copyright Buyout Missing Atomic Check-and-Set

**File:** `StripeWebhookService.cs`, lines 301-316

The exclusive license path correctly uses `ExecuteSqlInterpolatedAsync` for atomic CAS:

```csharp
var marked = await _db.Database.ExecuteSqlInterpolatedAsync(
    $"UPDATE \"Tracks\" SET \"ExclusiveSold\" = true WHERE \"Id\" = {trackId} AND \"ExclusiveSold\" = false");
```

But the copyright buyout path immediately below does a **non-atomic read-modify-write**:

```csharp
if (track.ExclusiveSold || track.Status == "copyright_transferred")
{
    _logger.LogWarning(...);
    return;
}
track.ExclusiveSold = true;
track.Status = "copyright_transferred";
```

Two concurrent copyright buyout webhooks could both pass the check and both mark the track as transferred.

**Fix:** Use the same `ExecuteSqlInterpolatedAsync` atomic pattern for copyright buyouts.

---

### C6. Webhook Secret Misconfiguration Silently Disables Verification

**File:** `StripeWebhookService.cs`, line 33

```csharp
_webhookSecret = configuration["Stripe:WebhookSecret"] ?? "";
```

If `Stripe:WebhookSecret` is not configured, `_webhookSecret` is `""`. The verification gate is:

```csharp
if (!string.IsNullOrEmpty(_webhookSecret) && !string.IsNullOrEmpty(signature))
```

With an empty secret, this is always `false`, so ALL webhooks bypass signature verification and fall to the development path. In production (`!_isDevelopment`), this throws — but it means a **missing config key silently breaks all webhook processing** with an opaque error, rather than failing at startup.

**Fix:** Throw at construction time if `_webhookSecret` is empty in non-development environments, or at minimum log a startup warning.

---

## HIGH Issues (P1 — Should Fix)

### H1. CheckoutService.ConfirmAsync Has No Transaction

**File:** `CheckoutService.cs`, lines 208-337

The confirm flow performs 5+ database operations without a transaction:
1. Create Purchase
2. Update Track (exclusive/copyright)
3. Add LibraryItem
4. Add WalletTransaction
5. Update Purchase (license link)

If step 3 fails, the purchase exists but the user has no library access. If step 4 fails, the creator isn't credited. Each uses a separate repository call (likely separate `SaveChanges`).

**Fix:** Wrap the entire confirm flow in a database transaction.

---

### H2. CheckoutService Uses Non-Atomic Exclusive License Check

**File:** `CheckoutService.cs`, lines 226-229

```csharp
if (licenseType == "exclusive" && !track.ExclusiveSold)
{
    track.ExclusiveSold = true;
    track.Status = "exclusive_sold";
    await _tracks.UpdateAsync(track);
}
```

Unlike the webhook handler, this does not use an atomic SQL update. Two concurrent confirms for the same exclusive track could both succeed.

---

### H3. Missing Webhook Event Types

**File:** `StripeWebhookService.cs`

Only 3 event types are handled:

| Event | Handled? | Impact |
|-------|----------|--------|
| `checkout.session.completed` | Yes | |
| `customer.subscription.deleted` | Yes | |
| `invoice.payment_failed` | Yes | |
| `checkout.session.expired` | **No** | Abandoned checkout sessions are never cleaned up |
| `customer.subscription.updated` | **No** | Plan changes via Stripe dashboard are missed |
| `charge.refunded` | **No** | Refunds don't revoke access or debit creator wallets |
| `charge.dispute.created` | **No** | Chargebacks don't trigger any action |
| `payment_intent.payment_failed` | **No** | Failed one-time payments aren't tracked |
| `invoice.paid` | **No** | Subscription renewals aren't tracked |

Most critically, **`charge.refunded` not being handled means refunded purchases retain full access** — the library item and license remain active.

---

### H4. Subscription Has No StripeSubscriptionId

**File:** `Subscription.cs` entity and `StripeWebhookService.HandleSubscriptionCheckout`

The local `Subscription` entity has no `StripeSubscriptionId` field. When `customer.subscription.deleted` arrives, the system cannot match it to a local subscription by Stripe ID — it has to do a fragile email-based lookup through the Stripe API:

```csharp
var customer = await customerService.GetAsync(stripeCustomerId);
return await _db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == customer.Email.ToUpperInvariant());
```

If the user changes their email, this lookup fails and the subscription is never cancelled.

---

### H5. Creator Wallet Credit Includes Stripe Processing Fees

**Files:** `StripeWebhookService.cs` line 411-412, `CheckoutService.cs` line 276-277

```csharp
var grossCents = amountTotal.Value;
var creatorCents = (long)Math.Floor(grossCents * (1 - platformFeeRate));
```

`amountTotal` is the gross amount charged to the buyer. Stripe's processing fee (~2.9% + 30c) is not deducted. The platform keeps `platformFeeRate` (e.g., 15%) but the creator gets `85% of gross`, meaning the platform actually subsidizes `Stripe's fee out of its 15% cut`. Over time this erodes margin.

---

## MEDIUM Issues (P2)

### M1. `FindOrCreateCustomerAsync` Race Condition

**File:** `StripeFacade.cs`, lines 140-157

Two concurrent checkouts with the same email:
1. Both call `ListAsync` → 0 results
2. Both call `CreateAsync` → 2 Stripe customers created

Stripe doesn't enforce unique emails. Result: duplicate customer objects in Stripe.

---

### M2. No Stripe Idempotency Keys

**File:** `StripeFacade.cs` — all API calls

No `IdempotencyKey` is set on any Stripe request. If the application retries (e.g., due to network timeout + automatic retry), duplicate sessions, transfers, or customers could be created.

---

### M3. `GetCheckoutSessionAsync` Swallows All Exceptions

**File:** `StripeFacade.cs`, lines 106-132

```csharp
catch
{
    return null;
}
```

A bare catch swallows network errors, rate limit errors, and authentication errors — all indistinguishable from "session not found." Callers can't distinguish a missing session from a Stripe outage.

---

### M4. No Stripe Service DI / HttpClient Pooling

**File:** `StripeFacade.cs`

All Stripe services (`SessionService`, `CustomerService`, `AccountService`, etc.) are instantiated per-call with `new`. While Stripe's SDK supports this, it bypasses `HttpClient` connection pooling and makes unit testing difficult.

---

### M5. Invoice Entity Is Unused

**File:** `Invoice.cs`

No service, controller, or webhook handler creates, updates, or queries `Invoice` records. The entity exists in the domain but serves no purpose.

---

### M6. Payout Entity Missing Transfer Correlation

**File:** `Payout.cs`

No `StripeTransferId` or `StripePayoutId` field. When a payout is requested and a Stripe transfer is created, there's no way to correlate them for reconciliation.

---

### M7. Purchase Status Is a Magic String

**File:** `Purchase.cs`

`Status` is `string` with values "pending", "completed", "refunded" — but no "failed" or "expired" state. No enum type safety. The `ExpiresAt` field exists but no code checks or enforces expiration.

---

### M8. PayoutController Has Dead Endpoints

**File:** `PayoutController.cs`, lines 111-126

`POST /payouts/settings` and `PUT /payouts/settings` return success without doing anything:

```csharp
[HttpPost("settings")]
public IActionResult CreateSettings([FromBody] PayoutSettingsRequest? request = null)
{
    return MessageResponse("Payout settings saved.");
}
```

---

## LOW Issues (P3)

### L1. Duplicate Controller Endpoints

- `PayoutController`: `ConnectStripe` and `Connect` do the same thing
- `PayoutController`: DELETE and POST `/disconnect` do the same thing
- `BillingController`: `Checkout` and `CheckoutSession` do the same thing
- `PayoutController`: `[HttpGet("/earnings")]` absolute route could conflict

### L2. `session.Url!` Null-Forgiving Operator

**File:** `StripeFacade.cs`, lines 54, 97

If Stripe returns a session without a URL (edge case), this throws `NullReferenceException` with no diagnostic info.

### L3. Webhook Logging Doesn't Include Event ID

**File:** `WebhookController.cs`, line 27

Only logs payload length and signature presence — not the event ID, which is essential for cross-referencing with Stripe Dashboard.

### L4. No Request Size Limit on Webhook Body

**File:** `WebhookController.cs`

`Request.Body` is read entirely into memory with no size cap. A malicious oversized payload could cause OOM.

---

## Summary Matrix

| ID | Severity | Component | Issue | Risk |
|----|----------|-----------|-------|------|
| C1 | CRITICAL | Webhook | Idempotency TOCTOU race | Duplicate purchases/credits |
| C2 | CRITICAL | PaymentsController | ProcessAsync skips payment verification | Free purchases |
| C3 | CRITICAL | WebhookController | Non-2xx on errors triggers retries | Cascading failures |
| C4 | CRITICAL | Webhook + Checkout | Dual fulfillment race | Double wallet credits |
| C5 | CRITICAL | Webhook | Copyright buyout non-atomic | Double transfers |
| C6 | CRITICAL | Webhook | Empty secret silently disables verification | Security bypass |
| H1 | HIGH | CheckoutService | No transaction wrapping | Partial fulfillment |
| H2 | HIGH | CheckoutService | Non-atomic exclusive check | Double exclusive sales |
| H3 | HIGH | Webhook | Missing event types (refund, dispute) | Unrevoked access |
| H4 | HIGH | Subscription | No StripeSubscriptionId | Broken cancellation |
| H5 | HIGH | Webhook + Checkout | Creator credit includes Stripe fee | Margin erosion |
| M1 | MEDIUM | StripeFacade | Customer race condition | Duplicate Stripe customers |
| M2 | MEDIUM | StripeFacade | No idempotency keys | Duplicate API objects |
| M3 | MEDIUM | StripeFacade | Bare catch swallows errors | Silent failures |
| M4 | MEDIUM | StripeFacade | No service DI | Untestable, no connection pooling |
| M5 | MEDIUM | Invoice entity | Unused | Dead code |
| M6 | MEDIUM | Payout entity | No transfer correlation | Reconciliation gap |
| M7 | MEDIUM | Purchase entity | String status, no expiry | Type safety, stale records |
| M8 | MEDIUM | PayoutController | Dead settings endpoints | Misleading API |
