# QA User Flow Validation Report

**Date:** 2026-03-19
**Scope:** End-to-end user flow analysis — Landing → Sign-up → Upload → Marketplace → Monetization
**Method:** Static code review of backend API (`Cambrian.Api` / .NET 8) and all supporting services
**Branch:** `cursor/user-flow-validation-c7b9`
**Status:** 10 of 15 issues fixed in this branch

---

## Executive Summary

The Cambrian Music Marketplace backend is a well-structured .NET 8 REST API with Clean Architecture. The core happy paths (register, upload, browse, purchase) are functional, but there were **15 issues** across the user journey — including 3 potentially broken flows, 5 points of significant friction, and 7 UX/consistency concerns that would confuse or frustrate real users.

### Fixes Applied

| Issue | Status |
|-------|--------|
| BUG-001: Creator role mismatch | **FIXED** |
| BUG-002: Webhook fee rate hardcoded | **FIXED** |
| BUG-003: Payment pricing wrong | **FIXED** |
| FRICTION-001: Password validation mismatch | **FIXED** |
| FRICTION-002: No email verification | Not addressed (requires frontend + email infra) |
| FRICTION-003: Stale JWT blocks uploads | **FIXED** |
| FRICTION-004: Storefront disabled | Not addressed (product decision) |
| FRICTION-005: No duration extraction | Not addressed (requires audio processing lib) |
| UX-001: Duplicate payment endpoints | Not addressed (breaking API change) |
| UX-002: Cache stale after upload | **FIXED** |
| UX-003: Stream auth inconsistency | Not addressed (by design for preview) |
| UX-004: No pagination metadata | **FIXED** |
| UX-005: Price double precision | **FIXED** |
| UX-006: Self-purchase allowed | **FIXED** |
| UX-007: Subscription webhook no-op | **FIXED** |

---

## Issues Found

### CRITICAL — Broken Flows

---

#### BUG-001: Registration returns wrong role for creators — JWT claims mismatch

**Severity:** Critical
**Flow:** Sign-up as Creator → Upload Track
**File:** `src/Cambrian.Application/Services/AuthService.cs` lines 62–92

**Description:**
When a user registers with `role: "creator"`, `AuthService.RegisterAsync` correctly sets `user.Tier = "creator"` on the database entity, but the `AuthResponse` hardcodes `Role = "User"` (line 90). The JWT token also embeds `ClaimTypes.Role = user.Role`, and `ApplicationUser.Role` defaults to `"User"` and is **never updated** during registration. This means:

1. The JWT `role` claim is always `"User"` regardless of the `role` field in the registration request.
2. The `AuthResponse.Role` is always `"User"`.
3. The `tier` JWT claim is correctly set to `"creator"`.

**Impact:** A newly registered creator can upload tracks (the `RequireCreatorTier` attribute checks the `tier` claim, not the `role` claim), but any frontend logic checking `role` will see `"User"` instead of `"Creator"`. Role-based authorization (`[Authorize(Roles = "Admin")]`) is unaffected since no endpoint requires `Role = "Creator"`, but this is confusing for the frontend.

**Steps to Reproduce:**
1. `POST /auth/register` with `{ "email": "...", "password": "...", "role": "creator" }`
2. Inspect the response: `user.role` is `"User"`, not `"Creator"`
3. Decode the JWT: the `role` claim is `"User"`, the `tier` claim is `"creator"`

**Expected:** `AuthResponse.Role` and the JWT `role` claim should reflect the creator role.

---

#### BUG-002: Webhook fee rate is hardcoded at 15% — ignores creator tier

**Severity:** Critical
**Flow:** Purchase Track → Creator gets paid
**File:** `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs` lines 402–425

**Description:**
The `StripeWebhookService.HandleTrackPurchase` method hardcodes a 15% platform fee rate (`const decimal platformFeeRate = 0.15m`). However, `TierManifest` defines:
- **Free creators:** 35% fee
- **Pro creators:** 15% fee

The `CheckoutService.ConfirmAsync` (the frontend-initiated confirmation path) correctly resolves the creator's tier and uses `TierManifest.For(creatorUser.CreatorTier).FeeRate`. But the webhook path — which is the authoritative server-to-server payment confirmation — always uses 15%.

**Impact:** Free-tier creators receive 85% of sales revenue instead of the correct 65%. This is a direct revenue loss for the platform.

**Steps to Reproduce:**
1. Register a free-tier creator and upload a track priced at $10.00
2. Purchase the track via Stripe Checkout
3. Stripe webhook fires `checkout.session.completed`
4. Webhook credits creator wallet with $8.50 (85%) instead of $6.50 (65%)

**Expected:** Webhook should resolve the creator's tier from the database and apply the correct fee rate.

---

#### BUG-003: `PaymentService.CreateCheckoutAsync` always uses legacy `track.Price` — ignores license-specific pricing

**Severity:** High
**Flow:** Purchase Track via `/payments/checkout` endpoint
**File:** `src/Cambrian.Application/Services/PaymentService.cs` lines 23–47

**Description:**
The `PaymentService.CreateCheckoutAsync` always computes price as `(int)(track.Price * 100)` — the legacy single-price field. It completely ignores `NonExclusivePriceCents`, `ExclusivePriceCents`, and `CopyrightBuyoutPriceCents`. This means exclusive and non-exclusive purchases through the `/payments/checkout` endpoint would be charged the wrong amount.

The `CheckoutService.CreateCheckoutAsync` (at `/checkout`) does correctly resolve license-specific pricing. This creates two parallel payment paths with different pricing logic.

**Steps to Reproduce:**
1. Upload a track with `price: 5.00`, `exclusivePrice: 50.00`
2. `POST /payments/checkout` with `{ "trackId": "...", "licenseType": "exclusive" }`
3. Stripe Checkout session is created for $5.00 instead of $50.00

**Expected:** Either deprecate the `/payments/checkout` endpoint or add license-type price resolution.

---

### HIGH — Significant Friction Points

---

#### FRICTION-001: Password validation mismatch between registration DTO and Identity rules

**Severity:** High
**Flow:** Sign-up
**Files:**
- `src/Cambrian.Application/DTOs/Auth/RegisterRequest.cs` — `[MinLength(8)]`
- `src/Cambrian.Application/DTOs/Auth/LoginRequest.cs` — `[MinLength(6)]`
- `src/Cambrian.Api/Program.cs` lines 59–67 — Identity requires digit, lowercase, uppercase, non-alphanumeric, length >= 8

**Description:**
A user enters `"password"` (8 chars, all lowercase). The DTO validation passes (`MinLength(8)` is satisfied), the request reaches `AuthService.RegisterAsync`, and then `UserManager.CreateAsync` fails with a compound error: `"Passwords must have at least one non alphanumeric character. Passwords must have at least one digit ('0'-'9'). Passwords must have at least one uppercase ('A'-'Z')."` This error is thrown as an `InvalidOperationException` with all failures concatenated by semicolons, which the `ExceptionMiddleware` converts to a 400 response.

**Impact:** The user sees a cryptic compound error message after submitting the form. They have no upfront guidance about the specific requirements. The error format (`Registration failed: Msg1; Msg2; Msg3`) is not user-friendly.

**Additional note:** The login DTO validates `[MinLength(6)]` even though registration requires 8 — a registered password can never be shorter than 8, so the login validation is misleadingly lenient.

**Steps to Reproduce:**
1. `POST /auth/register` with `{ "email": "test@example.com", "password": "password" }`
2. Response: `400 { "success": false, "error": "Registration failed: Passwords must have at least one non alphanumeric character.; Passwords must have at least one digit ('0'-'9').; Passwords must have at least one uppercase ('A'-'Z')." }`

**Expected:** DTO validation should mirror Identity rules, or the API should return structured error objects instead of concatenated strings.

---

#### FRICTION-002: No email verification — accounts are immediately active

**Severity:** High
**Flow:** Sign-up → Full access
**File:** `src/Cambrian.Application/Services/AuthService.cs`

**Description:**
After registration, the user is immediately issued a JWT and has full access. There is no email verification step. `ApplicationUser.EmailConfirmed` is only set to `true` during admin seed (line 438 of `Program.cs`) but never for regular users. This means:
- Anyone can register with a fake email and immediately upload/purchase
- Password reset (`/auth/forgot-password`) sends a code to the email, but if the email is fake, the user is permanently locked out
- No protection against spam accounts

**Impact:** Spam creators could flood the marketplace with junk tracks. Users who mistype their email lose recovery access.

---

#### FRICTION-003: Upload requires `creator` tier but the check is JWT-claim-based — stale tokens block uploads

**Severity:** Medium-High
**Flow:** Register as consumer → Upgrade to creator tier → Upload
**File:** `src/Cambrian.Api/Middleware/RequireCreatorTierAttribute.cs`

**Description:**
The `RequireCreatorTier` attribute reads the `tier` claim from the JWT (line 28). If a user registers as a consumer (`tier = "free"`), then upgrades their tier via `/subscriptions/update` or `/billing/checkout`, their existing JWT still contains `tier: "free"`. The attribute does fall back to a database lookup (line 31–41), but only if the `tier` claim is missing entirely — not if it's present but stale.

**Impact:** After upgrading, the user must call `GET /auth/me` (which re-issues a fresh JWT) or re-login before they can upload. This is a confusing gap — the upgrade appears successful but uploads fail with "Creator tier required."

**Steps to Reproduce:**
1. Register as `role: "user"` (tier = "free")
2. Upgrade via `/billing/checkout` → Stripe → `/billing/checkout-session/{id}` (tier updated in DB)
3. Immediately `POST /upload` with the original JWT → 403 "Creator tier required."
4. `GET /auth/me` → receive fresh token → retry upload → success

**Expected:** Either the middleware should always check the database, or the upgrade response should include a fresh JWT.

---

#### FRICTION-004: Creator storefront is disabled by default — new creators see "not available"

**Severity:** Medium
**Flow:** Creator → View Storefront
**File:** `src/Cambrian.Api/Controllers/CreatorProfileController.cs` line 52; `src/Cambrian.Api/Program.cs` lines 493–496

**Description:**
The `creator_storefront` feature flag is seeded as `enabled: false` on startup. The `GET /creator-profile/{slug}/storefront` endpoint returns 404 "Storefront is not available" when this flag is off. A creator who sets up their profile and shares their storefront URL will see a 404, with no explanation of why or how to enable it.

**Impact:** Creator onboarding dead-end. The profile endpoints (`GET /creator-profile/{slug}`, `PUT /creator-profile/me`) work, but the full storefront is gated behind an admin-only flag with no user-facing indication.

---

#### FRICTION-005: No track duration extraction — Duration field is always null for uploaded tracks

**Severity:** Medium
**Flow:** Upload Track → View in Marketplace
**Files:**
- `src/Cambrian.Application/Services/UploadService.cs` — no duration extraction logic
- `src/Cambrian.Application/DTOs/Catalog/UploadTrackRequest.cs` — no `Duration` field
- `src/Cambrian.Domain/Entities/Track.cs` — `Duration` property exists but is never populated

**Description:**
The `UploadTrackRequest` DTO does not include a `Duration` field, and `UploadService` does not extract duration from the audio file metadata. The `Track.Duration` property remains `null` for all uploaded tracks. The catalog filter `duration` and the track response `Duration` field are therefore non-functional.

**Impact:** Marketplace browse-by-duration filtering returns no results. Track cards show no duration, making it impossible for buyers to judge track length before listening.

---

### MEDIUM — UX Confusion / Consistency Issues

---

#### UX-001: Duplicate payment endpoints create confusion — `/checkout` vs `/payments/checkout`

**Severity:** Medium
**Flow:** Purchase Track

**Description:**
There are two separate checkout flows for track purchases:
1. `POST /checkout` → `CheckoutService` (correct pricing, proper confirm flow at `GET /checkout/session/{id}`)
2. `POST /payments/checkout` → `PaymentService` (wrong pricing per BUG-003, no proper confirm flow)

Additionally, `POST /purchases` (on `PaymentsController`) creates a purchase directly without Stripe verification, and `POST /payments/process` is a legacy path that only marks status without creating library items or licenses.

**Impact:** A frontend developer integrating with this API would be confused about which endpoint to use. Using the wrong one could result in incorrect pricing or missing library/license fulfillment.

---

#### UX-002: Catalog caching (30s) can show stale data after upload

**Severity:** Low-Medium
**Flow:** Upload Track → View in Marketplace
**File:** `src/Cambrian.Api/Controllers/CatalogController.cs` lines 15, 38–42

**Description:**
The `/discover` and `/catalog` endpoints cache results for 30 seconds. After uploading a track, the creator may not see it in the marketplace for up to 30 seconds. There is no cache invalidation triggered by uploads.

**Impact:** Creator uploads a track, navigates to marketplace, doesn't see it — thinks the upload failed. They may re-upload, creating duplicates.

---

#### UX-003: `GET /stream/{trackId}/audio` is anonymous but `GET /stream/{trackId}` requires auth

**Severity:** Low-Medium
**Flow:** Browse Marketplace → Play Track
**File:** `src/Cambrian.Api/Controllers/StreamController.cs`

**Description:**
- `GET /stream/{trackId}/audio` — `[AllowAnonymous]` — returns redirect to signed audio URL
- `GET /stream/{trackId}` — `[Authorize]` — returns JSON with stream URL
- `GET /stream` — `[Authorize]` — lists streamable tracks

The `/stream/{trackId}/audio` endpoint is the one catalog track `audioUrl` points to (set in `CatalogController.ResolveTrackUrls`). This means anonymous users can stream all tracks, which is the intended preview behavior. However, the inconsistency between the three `/stream` sub-endpoints is confusing for API consumers.

**Impact:** Minimal functional impact since the catalog correctly uses the anonymous endpoint, but API documentation is misleading.

---

#### UX-004: No pagination metadata in catalog responses

**Severity:** Medium
**Flow:** Browse Marketplace
**File:** `src/Cambrian.Application/Services/CatalogService.cs`

**Description:**
The `GET /discover`, `GET /catalog`, and `GET /trending` endpoints accept `page` and `pageSize` parameters, but the response only returns the track array — no `totalCount`, `totalPages`, `hasNextPage`, or `currentPage` metadata.

**Impact:** The frontend cannot build pagination controls, show "Showing X of Y results", or know when to stop requesting more pages. Users browsing a large catalog have no sense of how much content exists.

---

#### UX-005: Upload price field uses `double` — floating point precision issues

**Severity:** Medium
**Flow:** Upload Track → Set Price
**File:** `src/Cambrian.Application/DTOs/Catalog/UploadTrackRequest.cs` lines 24, 30–34

**Description:**
Price fields (`Price`, `NonExclusivePrice`, `ExclusivePrice`, `CopyrightBuyoutPrice`) are `double?` types. The conversion to cents uses `Math.Round(value * 100, MidpointRounding.AwayFromZero)`. While `MidpointRounding.AwayFromZero` helps, floating point representation of decimal values like `19.99` can introduce subtle rounding errors (`19.99 * 100 = 1998.9999999999998` in IEEE 754). The `Track.Price` field is also `double`.

**Impact:** Edge case where a creator sets a price like $29.99 and it's stored as 2998 cents or 3000 cents. For a financial application, `decimal` should be used throughout.

---

#### UX-006: Exclusive purchase allows buying your own track

**Severity:** Low-Medium
**Flow:** Creator → Purchase Own Track
**File:** `src/Cambrian.Application/Services/CheckoutService.cs`

**Description:**
Neither `CheckoutService.CreateCheckoutAsync` nor the webhook handler checks whether the buyer is the same user as the track creator. A creator could purchase their own track's exclusive license, paying themselves (minus the platform fee).

**Impact:** Potential for self-dealing or accidental self-purchases. The creator wallet credit logic would credit the creator for buying their own track.

---

#### UX-007: `HandleSubscriptionDeleted` webhook is a no-op — users aren't downgraded

**Severity:** High (operational)
**Flow:** Creator cancels subscription → Stripe sends webhook → Nothing happens
**File:** `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs` lines 515–529

**Description:**
The `HandleSubscriptionDeleted` method only logs a warning. It does not downgrade the user's tier. The comment acknowledges this: "We cannot reliably match Stripe customer ID to our user yet." Similarly, `HandleInvoicePaymentFailed` is also a log-only no-op.

The `SubscriptionService.CancelAsync` (called from `POST /subscriptions/cancel`) does properly downgrade the user. But if a subscription is cancelled from Stripe's side (e.g., failed payment after retries), the user retains Pro tier indefinitely.

**Impact:** Users whose subscriptions lapse at Stripe continue to enjoy Pro benefits (unlimited uploads, 15% fee rate) without paying.

---

### LOW — Minor Issues

---

#### MINOR-001: CSRF token endpoint returns a random GUID — not tied to any session

**Severity:** Low
**File:** `src/Cambrian.Api/Controllers/AuthController.cs` lines 113–117

**Description:**
`GET /auth/csrf-token` returns `{ "token": "<random-guid>" }` but nothing validates this token on subsequent requests. It appears to be a stub.

---

#### MINOR-002: Payout settings endpoints are stubs

**Severity:** Low
**File:** `src/Cambrian.Api/Controllers/PayoutController.cs` lines 111–126

**Description:**
`POST /payouts/settings` and `PUT /payouts/settings` return success messages but don't persist any data. A user configuring payout thresholds/schedule would see "saved" but nothing changes.

---

---

## Flow-by-Flow Summary

### 1. Landing on Site (Catalog Browsing)

| Step | Endpoint | Status | Issues |
|------|----------|--------|--------|
| View trending tracks | `GET /trending` | Works | UX-004: No pagination metadata |
| Browse catalog | `GET /catalog` | Works | UX-002: 30s cache delay after uploads |
| Search/filter tracks | `GET /discover?search=...` | Works | FRICTION-005: Duration filter broken |
| View track detail | `GET /tracks/{id}` | Works | — |
| Stream preview | `GET /stream/{id}/audio` | Works | UX-003: Auth inconsistency |

### 2. Sign-up

| Step | Endpoint | Status | Issues |
|------|----------|--------|--------|
| Register (consumer) | `POST /auth/register` | Works | FRICTION-001: Password validation mismatch |
| Register (creator) | `POST /auth/register` with `role: "creator"` | Works* | BUG-001: Response role always "User" |
| Email verification | N/A | Missing | FRICTION-002: No email verification |
| Login | `POST /auth/login` | Works | — |
| Get profile | `GET /auth/me` | Works | — |

### 3. Uploading a Track

| Step | Endpoint | Status | Issues |
|------|----------|--------|--------|
| Upload audio + metadata | `POST /upload` | Works | FRICTION-005: No duration extraction |
| Tier gating | `[RequireCreatorTier]` | Works* | FRICTION-003: Stale JWT blocks upgrades |
| Upload limit check | `UploadService` | Works | — |
| Cover art upload | `POST /upload` (CoverArt field) | Works | — |
| View own tracks | `GET /creator/tracks` | Works | — |

### 4. Viewing in Marketplace

| Step | Endpoint | Status | Issues |
|------|----------|--------|--------|
| Track appears in catalog | `GET /catalog` | Works* | UX-002: Up to 30s cache delay |
| Cover art resolves | `CatalogController.ResolveCoverArtUrl` | Works | — |
| Audio stream works | `GET /stream/{id}/audio` | Works | — |
| Creator storefront | `GET /creator-profile/{slug}/storefront` | Blocked | FRICTION-004: Feature flag disabled |

### 5. Monetization

| Step | Endpoint | Status | Issues |
|------|----------|--------|--------|
| Purchase track (correct path) | `POST /checkout` | Works | UX-006: Can buy own track |
| Purchase track (legacy path) | `POST /payments/checkout` | Broken pricing | BUG-003: Uses legacy price |
| Stripe redirect & pay | External | Works | — |
| Confirm purchase | `GET /checkout/session/{id}` | Works | — |
| Webhook fulfillment | `POST /webhook/stripe` | Works* | BUG-002: Wrong fee rate |
| Library access | `GET /library` | Works | — |
| Download purchased track | `GET /download/{id}` | Works | — |
| License certificate | `GET /licenses/{id}` | Works | — |
| Creator wallet credit | Automatic | Works* | BUG-002: Wrong amount for free creators |
| Creator payout (Stripe Connect) | `POST /payouts/connect-stripe` | Works | — |
| Subscription upgrade | `POST /billing/checkout` | Works | FRICTION-003: Stale JWT |
| Subscription cancellation (API) | `POST /subscriptions/cancel` | Works | — |
| Subscription cancellation (Stripe) | Webhook | Broken | UX-007: No-op handler |

---

## Recommended Priority Order

1. **BUG-002** — Fix webhook fee rate (revenue impact)
2. **UX-007** — Implement subscription downgrade on webhook
3. **BUG-003** — Fix or deprecate `/payments/checkout` pricing
4. **BUG-001** — Fix creator role in registration response
5. **FRICTION-001** — Align password validation with Identity rules
6. **FRICTION-003** — Return fresh JWT after tier upgrade
7. **FRICTION-002** — Add email verification flow
8. **UX-004** — Add pagination metadata to catalog
9. **FRICTION-005** — Extract audio duration on upload
10. **FRICTION-004** — Add user-facing storefront status messaging
11. **UX-005** — Migrate price fields from `double` to `decimal`
12. **UX-001** — Consolidate duplicate payment endpoints
13. **UX-006** — Prevent self-purchase
14. **UX-002** — Cache invalidation after upload
15. **UX-003** — Normalize stream auth consistency
