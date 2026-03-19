# FULL SYSTEM VALIDATION & DEEP USER FLOW AUDIT

**Date:** 2026-03-19  
**Auditor:** Platform Stability QA Agent  
**Scope:** Backend API (Cambrian .NET 8), all critical user flows  
**Status:** AUDIT COMPLETE — ISSUES IDENTIFIED

---

## TABLE OF CONTENTS

1. [System Health Summary](#1-system-health-summary)
2. [Payment System Status](#2-payment-system-status)
3. [Audio System Status](#3-audio-system-status)
4. [Critical Bugs (Must Fix Before Deploy)](#4-critical-bugs)
5. [Non-Critical Bugs](#5-non-critical-bugs)
6. [Root Causes by Category](#6-root-causes)
7. [Exact Files and Locations](#7-files-and-locations)
8. [Recommended Fix Order](#8-fix-order)
9. [Deployment Risk Assessment](#9-deployment-risk)

---

## 1. SYSTEM HEALTH SUMMARY

| Component | Status | Details |
|-----------|--------|---------|
| **API Startup** | OK | Fail-fast validation for JWT, Stripe, DB, FrontendUrl in production |
| **Database Connection** | OK | Supports ADO.NET and Render `postgres://` URI with auto-conversion; SSL enforced |
| **Migrations** | CAUTION | Non-production migration errors are swallowed — app may run on stale schema |
| **CORS** | OK | Properly configured; no `AllowAnyOrigin` with credentials |
| **Authentication** | OK | JWT with HMAC-SHA256, 24h expiry, proper claim mapping |
| **Rate Limiting** | OK | Per-IP fixed window; auth endpoints have stricter limits |
| **Security Headers** | OK | HSTS, nosniff, DENY framing, CSP, referrer policy |
| **Error Handling** | DEGRADED | ExceptionMiddleware has 3 bugs (see Critical #7, #8) |
| **Logging** | CAUTION | Startup logs leak admin email, JWT key length; S3 uses Console.WriteLine |
| **Graceful Shutdown** | BROKEN | Dockerfile shell-form ENTRYPOINT prevents SIGTERM delivery |

### Key Environment Findings

- No hardcoded credentials in production code paths
- No localhost leakage in production — development URLs properly gated
- All required env vars are validated at startup with clear error messages
- `.env.example` defaults to `ASPNETCORE_ENVIRONMENT=Staging` (should be `Development`)
- Swagger exposed on Staging (acceptable but noted)

---

## 2. PAYMENT SYSTEM STATUS

### **STATUS: DEGRADED — 6 Critical Issues, 5 High Issues**

The payment flow fundamentally works (checkout → Stripe → webhook → fulfillment) but has race conditions, a dangerous legacy endpoint, and missing webhook event handlers that together create real risk of financial loss.

### Payment Flow Architecture

```
User → POST /checkout → Stripe Checkout Session → redirect to Stripe
Stripe → POST /webhook/stripe → StripeWebhookService → fulfillment
User → GET /checkout/status → CheckoutService.ConfirmAsync → redundant fulfillment
```

### What Works

- Stripe checkout session creation with correct `clientReferenceId` format
- Webhook signature verification (when `Stripe:WebhookSecret` is configured)
- Webhook event deduplication via `StripeWebhookEvents` ledger with unique index on `EventId`
- Purchase creation with library item and license certificate issuance
- Exclusive license atomic check-and-set (in webhook path)
- Duplicate purchase prevention per user/track/license combination
- Creator wallet crediting with tier-based fee rates
- Subscription creation and cancellation via webhook

### Critical Payment Issues

| ID | Issue | Risk | Location |
|----|-------|------|----------|
| **PAY-C1** | `POST /payments/process` marks purchases "completed" without verifying Stripe payment | Users can claim tracks without paying | `PaymentService.cs:84-98` |
| **PAY-C2** | Both webhook handler AND `CheckoutService.ConfirmAsync` can fulfill the same payment simultaneously — no shared lock or transaction | Double creator wallet credits, duplicate library items | `StripeWebhookService.cs:266-468` + `CheckoutService.cs:112-338` |
| **PAY-C3** | Copyright buyout uses non-atomic check-and-set (unlike exclusive license which uses `ExecuteSqlInterpolatedAsync`) | Track copyright transferred to two buyers | `StripeWebhookService.cs:302-316` |
| **PAY-C4** | `CheckoutService.ConfirmAsync` runs 5+ DB operations without a transaction | Partial fulfillment — purchase without library access, or credit without purchase | `CheckoutService.cs:112-338` |
| **PAY-C5** | `CheckoutService` exclusive license check is non-atomic `if (!track.ExclusiveSold)` followed by separate `track.ExclusiveSold = true` | Two buyers get exclusive license concurrently | `CheckoutService.cs:226-231` |
| **PAY-C6** | Missing `Stripe:WebhookSecret` silently sets `""`, causing `if (!string.IsNullOrEmpty(_webhookSecret) && ...)` to skip verification entirely instead of failing at startup | Webhook spoofing in production | `StripeWebhookService.cs:33`, `StartupExtensions.cs` (no validation) |

### High Payment Issues

| ID | Issue | Risk | Location |
|----|-------|------|----------|
| **PAY-H1** | Missing webhook handlers for `charge.refunded`, `charge.dispute.created`, `checkout.session.expired` | Refunded purchases keep access; disputes go unnoticed | `StripeWebhookService.cs` |
| **PAY-H2** | `Subscription` entity has no `StripeSubscriptionId`; cancellation relies on Stripe customer email → local user email matching | Subscription never cancelled if user changes email | `StripeWebhookService.cs:589-607` |
| **PAY-H3** | Creator wallet credit uses gross `amountTotal` (before Stripe's ~2.9%+30c fee) | Platform subsidizes Stripe processing fee | `StripeWebhookService.cs:411-412`, `CheckoutService.cs:276-277` |
| **PAY-H4** | `WebhookController` returns `ErrorResponse()` (non-2xx) on processing errors, causing Stripe to retry for 72 hours | Cascading duplicate processing attempts | `WebhookController.cs:36-44` |
| **PAY-H5** | `PayoutController` has multiple duplicate endpoints and payout settings endpoints are no-ops returning fake success | Misleading API surface | `PayoutController.cs` |

---

## 3. AUDIO SYSTEM STATUS

### **STATUS: DEGRADED — 3 High Issues, 6 Medium Issues**

Audio streaming fundamentally works for the happy path but has access control gaps, architecture desync, and storage fallback issues.

### What Works

- Track upload via multipart form with extension validation
- Signed URL generation for S3/R2 storage
- Stream endpoint with redirect to CDN
- Download endpoint with purchase gating
- Stream session tracking (start/stop)

### Critical Audio Issues

| ID | Issue | Risk | Location |
|----|-------|------|----------|
| **AUD-H1** | `GET /stream/{trackId}/audio` is `[AllowAnonymous]` — full-quality audio accessible without authentication or purchase | Entire catalog streamable for free; product effectively given away | `StreamController.cs:75-90` |
| **AUD-H2** | Silent fallback to `LocalObjectStorage` when S3/R2 credentials are incomplete in non-production | Tracks uploaded during this window get local-path AudioUrl values; when S3 activates, those tracks become permanently unplayable — **silent data loss** | `StartupExtensions.cs:95-104` |
| **AUD-H3** | `UploadController` uses `[DisableRequestSizeLimit]` — Kestrel buffers entire request before UploadService validates 100MB limit | Memory exhaustion attack vector (multi-GB uploads) | `UploadController.cs` |

### Medium Audio Issues

| ID | Issue | Risk | Location |
|----|-------|------|----------|
| **AUD-M1** | `StreamController` does not use `IStreamService` — reimplements all logic inline, creating maintenance desync | Bug fixes in one place won't reflect in the other | `StreamController.cs` vs `StreamService.cs` |
| **AUD-M2** | S3 signed URLs expire in 1 hour but `DownloadService` tells client 15 minutes | Client-side expiration mismatch | `S3ObjectStorage.cs:68` vs `DownloadService.cs` |
| **AUD-M3** | MIME type validation skipped when `Content-Type` header is empty/null | Arbitrary file upload with allowed extension | `UploadService.cs:86` |
| **AUD-M4** | Non-atomic upload + DB write — file uploaded to storage, then DB record created | Orphaned files on DB failure | `UploadService.cs:103-174` |
| **AUD-M5** | Duplicate `SilentMp3Generator` implementations with different behavior | Developer confusion; wrong duration served | `Api/Tools/` vs `Infrastructure/Storage/` |
| **AUD-M6** | `LocalObjectStorage.GenerateSignedUrl` returns unsigned relative URL (`/uploads/key`) | Auth checks not exercised during local dev testing | `LocalObjectStorage.cs` |

---

## 4. CRITICAL BUGS (Must Fix Before Deploy)

These bugs represent immediate financial risk, data loss risk, or security vulnerabilities.

### BUG 1: Payment Bypass via Legacy `/payments/process` Endpoint
- **File:** `src/Cambrian.Application/Services/PaymentService.cs:84-98`
- **Issue:** `ProcessAsync()` marks any purchase as "completed" based solely on the caller's JWT, without verifying that Stripe actually received payment
- **Impact:** Authenticated users can claim tracks for free
- **Fix:** Remove this endpoint or add Stripe session verification

### BUG 2: Dual-Fulfillment Race Condition
- **File:** `src/Cambrian.Application/Services/CheckoutService.cs:112-338` + `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs:266-468`
- **Issue:** Both the webhook handler and `CheckoutService.ConfirmAsync` can fulfill the same payment simultaneously with no shared lock
- **Impact:** Double creator wallet credits, duplicate purchase records (only partially prevented)
- **Fix:** Use a DB advisory lock or unique constraint on (BuyerId, TrackId, LicenseType, StripeSessionId)

### BUG 3: Non-Atomic Copyright Buyout
- **File:** `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs:302-316`
- **Issue:** Copyright buyout uses read-then-write pattern unlike exclusive license which correctly uses `ExecuteSqlInterpolatedAsync`
- **Impact:** Track copyright could be transferred to two buyers in a race
- **Fix:** Use `ExecuteSqlInterpolatedAsync` atomic pattern (same as exclusive license at line 292)

### BUG 4: Non-Atomic Exclusive License in ConfirmAsync
- **File:** `src/Cambrian.Application/Services/CheckoutService.cs:226-231`
- **Issue:** `if (!track.ExclusiveSold) { track.ExclusiveSold = true; await _tracks.UpdateAsync(track); }` — not atomic
- **Impact:** Two concurrent buyers could both get an exclusive license
- **Fix:** Use `ExecuteSqlInterpolatedAsync` atomic CAS or a database lock

### BUG 5: CheckoutService Has No Transaction
- **File:** `src/Cambrian.Application/Services/CheckoutService.cs:112-338`
- **Issue:** 5+ DB operations (purchase insert, track update, library insert, wallet credit, license link) run without a wrapping transaction
- **Impact:** Partial fulfillment — user could have purchase without library access, or wallet credit without purchase record
- **Fix:** Wrap in `using var tx = await _db.Database.BeginTransactionAsync()`

### BUG 6: Webhook Secret Not Validated at Startup
- **File:** `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs:33`
- **Issue:** `_webhookSecret = configuration["Stripe:WebhookSecret"] ?? ""` — empty string silently disables verification
- **Impact:** In production, if `Stripe:WebhookSecret` is accidentally omitted, webhook signature verification is bypassed
- **Fix:** Add startup validation in `StartupExtensions.ValidateStripeKey` to require webhook secret in production

### BUG 7: ExceptionMiddleware Doesn't Check `Response.HasStarted`
- **File:** `src/Cambrian.Api/Middleware/ExceptionMiddleware.cs:34`
- **Issue:** Setting `Response.StatusCode` and writing to `Response.Body` after headers have been sent throws `InvalidOperationException`
- **Impact:** Streaming endpoints (audio) that throw mid-response crash with an unhandled exception
- **Fix:** Add `if (context.Response.HasStarted) { _logger.LogError(...); return; }` guard

### BUG 8: ExceptionMiddleware Returns 403 for Login Failures
- **File:** `src/Cambrian.Api/Middleware/ExceptionMiddleware.cs:37`
- **Issue:** `UnauthorizedAccessException` maps to HTTP 403 (Forbidden) instead of 401 (Unauthorized)
- **Impact:** Frontend may misinterpret login failures as permission denials; auth flow broken
- **Fix:** Change to `(int)HttpStatusCode.Unauthorized` (401)

### BUG 9: `GetSessionAsync` Returns Empty Token
- **File:** `src/Cambrian.Application/Services/AuthService.cs:125-138`
- **Issue:** `Token = ""` and `Role` is never set in the session response
- **Impact:** Frontend session restoration fails — user appears logged out after page refresh if relying on this endpoint for token refresh
- **Fix:** Call `GenerateJwt(user)` and include `Role` in the response

### BUG 10: CreatorTier Never Upgraded on Subscription Purchase
- **File:** `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs:475-511`
- **Issue:** `HandleSubscriptionCheckout` sets `user.Tier = tier` but never updates `user.CreatorTier` from `Free` to `Pro`
- **Impact:** `TierManifest.For(user.CreatorTier)` always returns Free config — paying Pro creators get 35% fee rate instead of 15%, and wrong upload limits
- **Fix:** Add `user.CreatorTier = tier == "pro" ? CreatorTier.Pro : CreatorTier.Free;` in `HandleSubscriptionCheckout`

### BUG 11: Dockerfile Shell-Form ENTRYPOINT
- **File:** `Dockerfile:25`
- **Issue:** `ENTRYPOINT sh -c "..."` runs .NET as a child of `sh`, preventing `SIGTERM` from reaching the process
- **Impact:** Non-graceful shutdown on container restart/deploy — in-flight requests dropped, DB connections not closed
- **Fix:** Use `ENTRYPOINT ["sh", "-c", "exec dotnet Cambrian.Api.dll"]` (add `exec`)

---

## 5. NON-CRITICAL BUGS

| ID | Severity | Issue | File | Line(s) |
|----|----------|-------|------|---------|
| **NC-1** | Medium | `CreatorProfileRepository` loads ALL rows via `ToListAsync()` then filters in-memory — O(n) for every query | `CreatorProfileRepository.cs` | 17, 27, 40, 84, 100, 116 |
| **NC-2** | Medium | `BrowseAsync()` loads all tracks into memory before `.Take()` | `StreamService.cs` | 21 |
| **NC-3** | Medium | Multiple null-forgiving operators (`!`) on `User.FindFirstValue()` across controllers | `PaymentsController.cs`, `DownloadController.cs`, `CreatorProfileController.cs` | Various |
| **NC-4** | Medium | `DownloadController.DownloadFile` streams entire file through API server — double network hop from S3 | `DownloadController.cs` | Various |
| **NC-5** | Medium | `S3ObjectStorage.NormaliseKey` fragile URL parsing — bucket name in path could corrupt key | `S3ObjectStorage.cs` | 141-158 |
| **NC-6** | Medium | `ExceptionMiddleware` exposes `ex.Message` for non-500 errors in production (e.g., `ArgumentException` may contain internal details) | `ExceptionMiddleware.cs` | 44-46 |
| **NC-7** | Medium | Missing FK constraints: `CreatorProfile.UserId`, `TrackCollection.CreatorId`, `AnalyticsEvent.UserId/TrackId` | `CambrianDbContext.cs` | 238-261 |
| **NC-8** | Medium | Invoice system entirely unused — no code creates or queries invoices despite having full entity/DbSet/controller | Multiple | — |
| **NC-9** | Medium | `RequestLoggingMiddleware` doesn't use try/finally — request timing lost on exceptions | `RequestLoggingMiddleware.cs` | — |
| **NC-10** | Low | `MemoryCache.Compact(1.0)` on upload evicts ALL cached data, not just catalog | `UploadController.cs` | — |
| **NC-11** | Low | `StreamController.Stop` doesn't verify stream session belongs to authenticated user | `StreamController.cs` | 117-125 |
| **NC-12** | Low | S3 storage uses `Console.WriteLine` instead of `ILogger` throughout | `S3ObjectStorage.cs` | Multiple |
| **NC-13** | Low | `AmazonS3Client` never disposed (registered as singleton) | `S3ObjectStorage.cs` | 33 |
| **NC-14** | Low | `SanitizeFilename` doesn't limit length — very long track titles exceed OS filename limits | `DownloadController.cs` | — |
| **NC-15** | Low | Orphaned domain entities (`User`, `CreatorBalance`, `TrackFile`, `ModerationAction`, `Payment`) with no DbSet or repository | `Domain/Entities/` | — |
| **NC-16** | Low | `UserRole` enum uses `Listener` but actual string role is `"User"` — naming mismatch (enum is unused dead code) | `Domain/Enums/` | — |
| **NC-17** | Low | `Payout` entity missing `StripeTransferId` for reconciliation | `Domain/Entities/Payout.cs` | — |
| **NC-18** | Low | `Purchase.ExpiresAt` field exists but nothing enforces it | `Domain/Entities/Purchase.cs` | — |
| **NC-19** | Low | Static file access blocking uses fragile `string.Contains("uploads")` matching | `Program.cs` | 210 |
| **NC-20** | Low | Startup logs leak admin email in plaintext and JWT key length | `StartupExtensions.cs` | 56, 287 |

---

## 6. ROOT CAUSES BY CATEGORY

### Environment

| Issue | Detail |
|-------|--------|
| `.env.example` defaults to Staging | Developers copying verbatim get staging behavior locally |
| Staging CORS includes localhost:5173 in `render.yaml` | Any local machine can make requests to staging API |
| Webhook secret not validated at startup | Empty string silently disables signature verification |
| Silent storage fallback in non-production | Tracks uploaded to local storage become unplayable when S3 activates |

### Schema / Data Integrity

| Issue | Detail |
|-------|--------|
| Missing FK constraints | `CreatorProfile.UserId`, `TrackCollection.CreatorId`, `AnalyticsEvent.UserId/TrackId` have no FK relationships |
| No unique constraint on webhook event ledger | `StripeWebhookEvents.EventId` has unique index (GOOD — confirmed in `CambrianDbContext.cs:153`) |
| `CreatorTier` vs `Tier` desync | Two overlapping fields never synchronized on subscription purchase |
| String-based status fields | `Purchase.Status`, `Subscription.Status` use raw strings instead of enums — typo risk |
| Orphaned domain entities | 5+ entity classes with no DbSet or repository |

### Backend

| Issue | Detail |
|-------|--------|
| Dual fulfillment paths | Webhook and ConfirmAsync both fulfill without coordination |
| Non-atomic operations | Copyright buyout, ConfirmAsync exclusive check, upload + DB write |
| Missing transaction wrapping | `CheckoutService.ConfirmAsync` — 5+ operations without transaction |
| Legacy payment bypass | `POST /payments/process` marks completed without verification |
| Full-table scans | `CreatorProfileRepository` loads all rows for every query |
| Service layer bypass | `StreamController` and `DownloadController` inject repositories directly |

### Frontend-Facing

| Issue | Detail |
|-------|--------|
| Session endpoint returns empty token | `GetSessionAsync` sets `Token = ""` — breaks token refresh |
| 403 instead of 401 for auth failures | `UnauthorizedAccessException` → 403 Forbidden instead of 401 |
| Anonymous audio streaming | `StreamAudio` is `[AllowAnonymous]` — full audio accessible without purchase |
| URL expiration mismatch | S3 URLs valid 1h, client told 15min |

---

## 7. EXACT FILES AND LOCATIONS

### Payment System Files

| File | Issues |
|------|--------|
| `src/Cambrian.Application/Services/PaymentService.cs:84-98` | PAY-C1: Payment bypass |
| `src/Cambrian.Application/Services/CheckoutService.cs:112-338` | PAY-C2, C4, C5: Race conditions, no transaction |
| `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs:33` | PAY-C6: Webhook secret empty string |
| `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs:302-316` | PAY-C3: Non-atomic copyright buyout |
| `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs:475-511` | BUG 10: CreatorTier not upgraded |
| `src/Cambrian.Api/Controllers/WebhookController.cs:36-44` | PAY-H4: Non-2xx on error |

### Audio System Files

| File | Issues |
|------|--------|
| `src/Cambrian.Api/Controllers/StreamController.cs:75-90` | AUD-H1: Anonymous full audio access |
| `src/Cambrian.Api/StartupExtensions.cs:95-104` | AUD-H2: Silent local storage fallback |
| `src/Cambrian.Api/Controllers/UploadController.cs` | AUD-H3: DisableRequestSizeLimit |
| `src/Cambrian.Infrastructure/Storage/S3ObjectStorage.cs:68` | AUD-M2: URL expiration mismatch |

### Auth / User Flow Files

| File | Issues |
|------|--------|
| `src/Cambrian.Application/Services/AuthService.cs:125-138` | BUG 9: Empty token in session |
| `src/Cambrian.Api/Middleware/ExceptionMiddleware.cs:34-37` | BUG 7, 8: HasStarted, 403 vs 401 |
| `src/Cambrian.Persistence/Repositories/CreatorProfileRepository.cs:17-116` | NC-1: Full table scans |
| `src/Cambrian.Persistence/CambrianDbContext.cs:238-261` | NC-7: Missing FK constraints |

### Infrastructure Files

| File | Issues |
|------|--------|
| `Dockerfile:25` | BUG 11: Shell-form ENTRYPOINT |
| `src/Cambrian.Api/Program.cs:210` | NC-19: Fragile static file blocking |
| `src/Cambrian.Api/StartupExtensions.cs:56,287` | NC-20: Log leakage |

---

## 8. RECOMMENDED FIX ORDER (By Risk)

### Tier 1: IMMEDIATE — Financial & Security Risk

| Priority | Bug ID | Fix | Effort | Risk if Unfixed |
|----------|--------|-----|--------|-----------------|
| **1** | PAY-C1 | Remove or gate `POST /payments/process` behind admin-only or add Stripe session verification | Single file | Users can steal tracks |
| **2** | PAY-C6 | Add `Stripe:WebhookSecret` validation in `StartupExtensions.ValidateStripeKey` for production | Single file, ~5 lines | Webhook spoofing |
| **3** | PAY-C3 | Use `ExecuteSqlInterpolatedAsync` atomic CAS for copyright buyout (copy exclusive license pattern) | Single file, ~10 lines | Double copyright transfer |
| **4** | BUG 10 | Add `user.CreatorTier = CreatorTier.Pro` in `HandleSubscriptionCheckout` | Single file, 1 line | Pro creators overcharged |

### Tier 2: HIGH — Data Integrity

| Priority | Bug ID | Fix | Effort | Risk if Unfixed |
|----------|--------|-----|--------|-----------------|
| **5** | PAY-C4 | Wrap `CheckoutService.ConfirmAsync` in a DB transaction | Single file, ~10 lines | Partial fulfillment |
| **6** | PAY-C5 | Use atomic CAS for exclusive license in `CheckoutService` | Single file, ~10 lines | Double exclusive sale |
| **7** | PAY-C2 | Add DB advisory lock or unique constraint to prevent dual fulfillment | 2 files, ~20 lines | Double wallet credits |
| **8** | PAY-H1 | Add webhook handlers for `charge.refunded`, `charge.dispute.created` | Single file, ~50 lines | Refunded users keep access |

### Tier 3: HIGH — User Experience

| Priority | Bug ID | Fix | Effort | Risk if Unfixed |
|----------|--------|-----|--------|-----------------|
| **9** | BUG 9 | Generate fresh JWT in `GetSessionAsync` and include `Role` | Single file, ~5 lines | Session restoration broken |
| **10** | BUG 8 | Change `UnauthorizedAccessException` → 401 in ExceptionMiddleware | Single file, 1 line | Login flow confused |
| **11** | BUG 7 | Add `Response.HasStarted` guard in ExceptionMiddleware | Single file, ~3 lines | Streaming errors crash |

### Tier 4: MEDIUM — Stability & Performance

| Priority | Bug ID | Fix | Effort | Risk if Unfixed |
|----------|--------|-----|--------|-----------------|
| **12** | BUG 11 | Add `exec` to Dockerfile ENTRYPOINT | Single file, 1 line | Non-graceful deploys |
| **13** | AUD-H1 | Add `[Authorize]` to `StreamAudio` or implement preview truncation | Single file, 1 line (or ~20 for preview) | Catalog given away free |
| **14** | AUD-H3 | Replace `[DisableRequestSizeLimit]` with `[RequestSizeLimit(150*1024*1024)]` | Single file, 1 line | Memory exhaustion attack |
| **15** | NC-1 | Replace `ToListAsync()` + foreach with `FirstOrDefaultAsync(predicate)` in CreatorProfileRepository | Single file, ~30 lines | O(n) queries on every request |
| **16** | PAY-H4 | Return 200 OK from webhook controller even on processing errors (log errors, don't propagate) | Single file, ~5 lines | 72h retry storms |

### Tier 5: LOW — Cleanup

| Priority | Bug ID | Fix | Effort |
|----------|--------|-----|--------|
| **17** | NC-7 | Add FK constraints for CreatorProfile, TrackCollection, AnalyticsEvent | Migration + DbContext |
| **18** | NC-3 | Replace `!` null-forgiving operators with proper null checks | Multiple files |
| **19** | AUD-M1 | Refactor StreamController to use IStreamService | Single file |
| **20** | NC-12 | Replace Console.WriteLine with ILogger in S3ObjectStorage | Single file |

---

## 9. DEPLOYMENT RISK ASSESSMENT

### Overall Risk: **HIGH**

The platform can serve basic functionality (registration, login, browsing, uploading) but the payment system has exploitable vulnerabilities and race conditions that represent real financial risk.

### Risk Matrix

| Area | Risk Level | Rationale |
|------|------------|-----------|
| **Authentication** | LOW | JWT flow is solid; password reset properly hashed; rate limiting in place |
| **Payments** | **CRITICAL** | Legacy bypass endpoint, dual fulfillment race, missing refund handlers |
| **Audio Streaming** | **HIGH** | Anonymous access to full audio; storage fallback creates silent data loss |
| **Data Integrity** | **MEDIUM** | Missing FK constraints; CreatorTier/Tier desync; full-table scans |
| **Infrastructure** | **MEDIUM** | Non-graceful shutdown; memory exhaustion vector; log leakage |
| **User Experience** | **MEDIUM** | Empty session tokens; 403/401 confusion; URL expiration mismatch |

### Pre-Deployment Checklist

Before any new features are implemented:

- [ ] Fix PAY-C1: Remove or secure `/payments/process` endpoint
- [ ] Fix PAY-C6: Validate `Stripe:WebhookSecret` at startup in production
- [ ] Fix BUG 10: Upgrade `CreatorTier` on subscription purchase
- [ ] Fix PAY-C3: Atomic copyright buyout
- [ ] Fix BUG 9: Session token refresh
- [ ] Fix BUG 8: 401 vs 403 mapping
- [ ] Fix BUG 7: `Response.HasStarted` guard
- [ ] Fix BUG 11: Dockerfile ENTRYPOINT
- [ ] Verify Stripe webhook secret is configured in production
- [ ] Verify S3/R2 storage credentials are complete in production
- [ ] Verify no tracks have local-path AudioUrl values in production DB

### Recommendation

**Do not deploy new features until Tier 1 and Tier 2 fixes (priorities 1-8) are completed and verified.** The payment bypass (PAY-C1) and webhook secret bypass (PAY-C6) are especially urgent as they represent exploitable security vulnerabilities. The CreatorTier desync (BUG 10) is silently overcharging Pro creators. All three are single-file, low-effort fixes.

---

*Report generated by automated platform stability audit. All findings traced to specific source files and line numbers. No data was modified during this audit.*
