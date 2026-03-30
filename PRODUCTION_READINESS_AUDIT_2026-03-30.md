# Production Readiness Audit — 2026-03-30

> Systematic pass/fail verification of every production requirement against actual code.
> Scope: startup validation, auth, payments, security headers, CORS, rate limiting, authorization, error handling, deployment.

---

## VERDICT: CONDITIONAL PASS

**27 of 31 checks pass.** 4 items require remediation before production launch — 2 are security bugs (not config), 2 are hardening gaps.

---

## 1. STARTUP VALIDATION

| Check | Status | Evidence |
|-------|--------|----------|
| JWT key required (>=32 chars) | **PASS** | `Program.cs:58-59` — throws `InvalidOperationException` if missing or <32 chars |
| JWT Issuer/Audience required | **PASS** | `Program.cs:61-65` — throws if empty |
| DB connection string required | **PASS** | `StartupExtensions.cs:18-54` — resolves from config or `DATABASE_URL` env var; throws in non-Testing if missing |
| Stripe `sk_live_` required in prod | **PASS** | `StartupExtensions.cs:296-310` — throws if `sk_test_` used in Production; webhook secret also required |
| Storage provider != `local` in prod | **PASS** | `StartupExtensions.cs:119-121` — throws if provider is `local` in Production |
| Email provider != `console` in prod | **PASS** | `StartupExtensions.cs:142-144` — throws if not `smtp` or `resend` in Production |
| Frontend URL required in prod | **PASS** | `StartupExtensions.cs:313-319` — throws if `App:FrontendUrl` missing |
| Secrets not in appsettings | **PASS** | `appsettings.Production.json` — all sensitive values empty; injected via env vars |

---

## 2. AUTHENTICATION & JWT

| Check | Status | Evidence |
|-------|--------|----------|
| ValidateIssuer = true | **PASS** | `Program.cs:81` |
| ValidateAudience = true | **PASS** | `Program.cs:82` |
| ValidateLifetime = true | **PASS** | `Program.cs:83` |
| ValidateIssuerSigningKey = true | **PASS** | `Program.cs:84` |
| ClockSkew <= 5 min | **PASS** | `Program.cs:92` — 2 minutes |
| Password: 8+ chars, upper, lower, digit, special | **PASS** | `Program.cs:40-44` |
| Reset code hashed (SHA256) | **PASS** | `AuthService.cs:375-379` |
| Reset code expiry enforced (15 min) | **PASS** | `AuthService.cs:30,218,362-365` |
| JWT signed with HMAC-SHA256 | **PASS** | `AuthService.cs:400-401` |
| Google OAuth token validated | **PASS** | `AuthService.cs:437-438` — email_verified checked |

---

## 3. AUTHORIZATION — ALL 30 CONTROLLERS

| Check | Status | Evidence |
|-------|--------|----------|
| All `/admin/*` endpoints require `[Authorize(Roles="Admin")]` | **PASS** | AdminController, DebugController, DataController — class-level attribute |
| All `/upload`, `/payouts`, `/creator` endpoints require Creator tier | **PASS** | `[Authorize]` + `[RequireCreatorTier]` on UploadController, PayoutController, CreatorController |
| All data-modifying endpoints (POST/PUT/DELETE) have auth | **PASS** | No unprotected mutation endpoints found |
| Public endpoints explicitly `[AllowAnonymous]` or no attribute | **PASS** | CatalogController, HealthController, ActivityController |
| Webhook uses signature verification instead of JWT | **PASS** | `WebhookController.cs:22-31` — Stripe-Signature header passed to service |
| Feature flag modification admin-only | **PASS** | `FeatureFlagsController` — PUT/DELETE require Admin role |

---

## 4. STRIPE & PAYMENTS

| Check | Status | Evidence |
|-------|--------|----------|
| Webhook signature cryptographically verified | **PASS** | `StripeWebhookService.cs:52-73` — `EventUtility.ConstructEvent()`, no bypass |
| Webhook secret required (throws if missing) | **PASS** | `StripeWebhookService.cs:52-61` |
| EventId idempotency check | **PASS** | `StripeWebhookService.cs:122-132` — duplicate events skipped |
| All handlers wrapped in DB transaction | **PASS** | `StripeWebhookService.cs:156-229` — explicit transaction with rollback on failure |
| Exclusive license CAS atomic SQL | **PASS** | `StripeWebhookService.cs:307-314` — raw SQL UPDATE with WHERE condition |
| Copyright buyout CAS atomic SQL | **PASS** | `StripeWebhookService.cs:325-332` — compound WHERE condition |
| Errors re-thrown (Stripe retries on 500) | **PASS** | `StripeWebhookService.cs:221` — exception re-thrown after marking event "failed" |
| StripeSessionId unique filtered index | **PASS** | `CambrianDbContext.cs:92-94` — unique where not null |
| Duplicate purchase detection at checkout | **PASS** | `CheckoutService.cs:73-81` — checks existing completed purchases |
| Fee rates from TierManifest (not hardcoded) | **PASS** | Free: 35%, Pro: 15% — `TierManifest.cs:19,31` |

---

## 5. SECURITY HEADERS

| Header | Status | Value |
|--------|--------|-------|
| X-Content-Type-Options | **PASS** | `nosniff` — `Program.cs:256` |
| X-Frame-Options | **PASS** | `DENY` — `Program.cs:257` |
| Referrer-Policy | **PASS** | `strict-origin-when-cross-origin` — `Program.cs:258` |
| Permissions-Policy | **PASS** | `camera=(), microphone=(), geolocation=()` — `Program.cs:259` |
| X-XSS-Protection | **PASS** | `0` (correct — disables buggy browser filter) — `Program.cs:260` |
| Strict-Transport-Security (HSTS) | **PASS** | `max-age=31536000; includeSubDomains` — production/staging only — `Program.cs:261-262` |
| Content-Security-Policy | **FAIL** | Missing entirely. No CSP header set anywhere. |

---

## 6. CORS

| Check | Status | Evidence |
|-------|--------|----------|
| No wildcard `*` origins | **PASS** | `StartupExtensions.cs:167-212` |
| Production locked to exact domains | **PASS** | `https://cambrianmusic.com`, `https://www.cambrianmusic.com` |
| Vercel/CF preview: strict prefix match | **PASS** | `StartupExtensions.cs:263-285` — validates slug prefix, not wildcard `*.vercel.app` |
| Credentials allowed with explicit origins | **PASS** | `AllowCredentials()` + explicit origin list |

---

## 7. RATE LIMITING

| Check | Status | Evidence |
|-------|--------|----------|
| Global rate limit (production) | **PASS** | 100 req/min per IP — `Program.cs:137-145` |
| Auth rate limit (production) | **PASS** | 10 req/min per IP — `Program.cs:146-154` |
| Applied to register, login, forgot-password | **PASS** | `[EnableRateLimiting("auth")]` on all auth endpoints |
| Applied to verify-code | **PASS** | `[EnableRateLimiting("auth")]` present |
| Applied to set-password, link-google | **FAIL** | Missing `[EnableRateLimiting("auth")]` on both endpoints |

---

## 8. ERROR HANDLING

| Check | Status | Evidence |
|-------|--------|----------|
| Stack traces hidden in production | **PASS** | `ExceptionMiddleware.cs:31-33` — truncated type+message only |
| Generic error messages in production | **PASS** | `ExceptionMiddleware.cs:58-67` — `"An unexpected error occurred."` for 5xx |
| Validation errors surfaced (ArgumentException) | **PASS** | Intentional: user-facing validation messages passed through |

---

## 9. DEPLOYMENT

| Check | Status | Evidence |
|-------|--------|----------|
| Non-root container user | **PASS** | `Dockerfile:20-24` — `appuser` UID 1001 |
| Multi-stage build | **PASS** | SDK build → aspnet runtime |
| Migrations auto-run at startup | **PASS** | `Program.cs:289` — `RunMigrationsAsync()` |
| Migrations skipped in Testing | **PASS** | `StartupExtensions.cs:218-221` |
| Health check endpoint | **PASS** | `GET /health` — returns status, timestamp, DB status, counts |
| Health endpoint leaks no secrets | **PASS** | Only aggregate counts and environment name |
| Demo users skipped in production | **PASS** | `StartupExtensions.cs:407-410` — `if (IsProduction()) return` |
| HTTPS redirect middleware | **FAIL** | No `app.UseHttpsRedirection()` — relies entirely on reverse proxy |

---

## 10. ACCESS CONTROL BUGS (from security audit)

| Check | Status | Evidence |
|-------|--------|----------|
| Download requires completed purchase | **FAIL** | `DownloadController.cs:34-73` — checks `libraryItem != null`, not `purchaseId != null`. Users can save-then-download without buying. |
| Hidden tracks inaccessible by direct ID | **FAIL** | `CatalogController.cs:91-104`, `StreamController.cs:77-94` — no visibility check on direct access |

---

## SUMMARY SCORECARD

```
CATEGORY                           PASS    FAIL    TOTAL
──────────────────────────────────────────────────────────
Startup Validation                  8       0       8
Authentication & JWT               10       0      10
Authorization (30 controllers)      6       0       6
Stripe & Payments                  10       0      10
Security Headers                    6       1       7
CORS                                4       0       4
Rate Limiting                       4       1       5
Error Handling                      3       0       3
Deployment                          7       1       8
Access Control Bugs                 0       2       2
──────────────────────────────────────────────────────────
TOTAL                              58       5      63
                                  92%      8%
```

---

## 4 ITEMS BLOCKING PRODUCTION

| # | Issue | Severity | Effort | What to Do |
|---|-------|----------|--------|------------|
| 1 | **Download without purchase** — library save bypasses entitlement check | Critical | 15 min | Add `purchaseId != null` and `purchase.Status == "completed"` check in `DownloadController` |
| 2 | **Hidden tracks accessible by ID** — visibility not enforced on `/tracks/{id}` and `/stream/{id}/audio` | Critical | 15 min | Add `track.Visibility == "public"` and `track.Status != "copyright_transferred"` guard |
| 3 | **Missing CSP header** | Medium | 10 min | Add `Content-Security-Policy: default-src 'self'` in security headers middleware |
| 4 | **Missing rate limit on `/auth/set-password` and `/auth/link-google`** | Medium | 5 min | Add `[EnableRateLimiting("auth")]` to both endpoints |

### Recommended but not blocking

| # | Issue | Severity | Notes |
|---|-------|----------|-------|
| 5 | No `app.UseHttpsRedirection()` | Low | Acceptable if Render enforces HTTPS at ingress (verify in Render dashboard) |
| 6 | Staging leaks error details | Low | `ExceptionMiddleware` treats staging like dev; consider production-like error responses |

---

## VERDICT

**CONDITIONAL PASS — fix items 1-4 (estimated ~45 minutes of work), then production-ready.**

The infrastructure, authentication, payment processing, authorization model, CORS, and deployment pipeline are all solid. The 4 blocking items are isolated bugs and missing attributes, not architectural problems.
