# Cambrian API — Subscriptions & Entitlements (Phase 1)

Backend contract for the subscription-tier + entitlement surface. The frontend should
build against these exact shapes. All responses use the standard envelope
`{ "success": bool, "data": <payload>, "message"?: string, "error"?: string }` unless
noted (the 402 upgrade response is a flat object — see below).

Auth: send the JWT as `Authorization: Bearer <token>` or rely on the `auth_token` HttpOnly
cookie. All endpoints below require auth except `POST /api/stripe/webhook`.

## Tiers

| Plan | Slug | Price | Max tracks | Platform fee |
|------|------|-------|-----------|--------------|
| Free | `free` | $0 | 10 | 35% |
| Creator | `creator` | $15/mo | unlimited | 15% |
| Pro / Label | `pro` | $39/mo | unlimited | 10% |

---

## GET /api/me/entitlements

Resolve the caller's plan-level entitlements. Source of truth for feature gating.

**200** → `data`:
```json
{
  "plan": "creator",
  "status": "active",
  "limits": { "maxTracks": null },
  "features": {
    "provenanceStamp": true,
    "complianceScoreRead": true,
    "unlimitedTracks": true,
    "fullProvenanceSuite": true,
    "pdfCertificates": true,
    "commercialRightsVerification": true,
    "verifiedCleanBadge": true,
    "ddexC2pa": true,
    "routingGuidance": true,
    "catalogAnalytics": true,
    "copyrightOfficeAssist": false,
    "bulkUpload": false,
    "syncPool": false,
    "apiAccess": false,
    "prioritySupport": false
  }
}
```
- `plan`: `free` | `creator` | `pro`
- `status`: `active` | `cancelled` | `expired` | `past_due` (Free is always `active`)
- `limits.maxTracks`: integer, or `null` for unlimited
- `features`: boolean map. Free enables only `provenanceStamp` + `complianceScoreRead`;
  Creator adds the full suite; Pro adds `copyrightOfficeAssist`, `bulkUpload`, `syncPool`,
  `apiAccess`, `prioritySupport`.

---

## POST /api/billing/checkout

Start a Stripe Checkout session for a subscription tier.

**Body:** `{ "tier": "creator" }` — `tier` ∈ { `creator`, `pro` }.

**200** → `data`:
```json
{ "checkoutUrl": "https://checkout.stripe.com/c/pay/cs_test_..." }
```
Redirect the browser to `checkoutUrl`. Fulfillment happens via the webhook
(`checkout.session.completed`); the `GET /billing/checkout-session/{sessionId}` endpoint
also confirms on return.

**400** — invalid tier, or Admin account. **401** — unauthenticated.

---

## POST /api/billing/portal

Open the Stripe Customer Portal for self-service management (upgrade/downgrade/cancel/
payment method).

**Body:** none.

**200** → `data`:
```json
{ "portalUrl": "https://billing.stripe.com/p/session/..." }
```
Redirect the browser to `portalUrl`.

**400** — no Stripe customer and no email on file. **401** — unauthenticated.

---

## POST /api/tracks

Create (upload) a track. Enforces the Free 10-track limit server-side.
`multipart/form-data` with at least `Title` and an `Audio` file (same form fields as the
legacy `POST /upload`).

**201** → `data`: the created track (`trackId`, `cambrianTrackId`, `title`, …).

**402** — track limit reached. **Flat body (not the standard envelope):**
```json
{ "success": false, "error": "Upload limit reached. The Free plan allows 10 tracks. Upgrade to Creator or Pro for unlimited uploads.", "code": "UPGRADE_REQUIRED" }
```
Detect `code === "UPGRADE_REQUIRED"` to trigger the upgrade flow.

**400** — validation error. **401/403** — auth / not a creator / email unverified.

---

## POST /api/stripe/webhook

Stripe → backend. Requires a valid `Stripe-Signature` header (signature verification is
mandatory). Not called by the frontend. Handled events: `checkout.session.completed`,
`customer.subscription.updated`, `customer.subscription.deleted`, `invoice.paid`,
`invoice.payment_failed` (plus refund/dispute events). Idempotent via `StripeWebhookEvents`.

**200** — acknowledged. **400** — bad/missing signature. **500** — processing error (Stripe retries).

---

## Configuration (backend, not committed)

Set these in the environment / secrets store (never in source):

| Key | Purpose |
|-----|---------|
| `Stripe:SecretKey` (`STRIPE_SECRET_KEY`) | Stripe API key (Billing + Connect) |
| `Stripe:WebhookSecret` (`STRIPE_WEBHOOK_SECRET`) | Webhook signature secret |
| `Stripe:ConnectWebhookSecret` (`STRIPE_CONNECT_WEBHOOK_SECRET`) | Stripe Connect webhook signature secret for tips/fan subscriptions |
| `Stripe:Prices:Creator` | Stripe **Price ID** for the $15 Creator plan |
| `Stripe:Prices:Pro` | Stripe **Price ID** for the $39 Pro/Label plan |
| `App:FrontendUrl` | Base URL for checkout/portal return redirects |

Subscription Billing is kept separate from the existing Stripe **Connect** payout flow.
