# Cambrian Platform — Comprehensive Security & Vulnerability Audit

**Date:** 2026-03-22
**Scope:** Backend (.NET API), Creator Identity, File Uploads, Payments, DTOs, Auth, Database, API Contracts, Logging
**Methodology:** Static code analysis of all controllers, services, repositories, DTOs, middleware, and configuration files
**Frontend Note:** No frontend code exists in this repository (separate Next.js/React repo on Vercel). Frontend audit was not possible.

---

## Executive Summary

The Cambrian platform demonstrates a generally solid security posture — JWT authentication is properly configured, admin endpoints are role-gated, Entity Framework prevents SQL injection, and security headers are comprehensive. However, the audit identified **6 CRITICAL**, **11 HIGH**, **15 MEDIUM**, and **10 LOW** severity issues across payments, authentication, file uploads, and data exposure that require remediation.

The most urgent findings involve:
- A **payout race condition** enabling withdrawal of more funds than available
- **Stripe session hijacking** allowing free purchases using another user's payment session
- **Creator email leakage** in public catalog APIs
- **Hardcoded demo credentials** seeded in production
- **Anonymous access to full audio files** bypassing the purchase model

---

## CRITICAL Vulnerabilities (6)

### C1. Payout Race Condition — Balance Check and Debit Are Not Atomic
- **Severity:** CRITICAL
- **File:** `src/Cambrian.Application/Services/PayoutService.cs:97-122`
- **Issue:** `RequestAsync` reads wallet balance, then later debits via separate non-transactional calls. Unlike `WalletService.WithdrawAsync` which uses `AtomicWithdrawAsync` with serializable isolation, PayoutService uses plain `GetBalanceAsync` + `AddTransactionAsync`.
- **Exploit:** An attacker sends multiple concurrent payout requests. All read the same positive balance before any debit is written. Each passes the balance check and issues a Stripe transfer, draining more money than available. Classic TOCTOU bug.
- **Fix:** Use `WalletRepository.AtomicWithdrawAsync` (serializable isolation) instead of the separate read-then-write pattern.

### C2. PurchaseService Does Not Verify Stripe Session Belongs to Requesting User
- **Severity:** CRITICAL
- **File:** `src/Cambrian.Application/Services/PurchaseService.cs:42-52`
- **Issue:** The method verifies the Stripe session is paid but never checks that `session.ClientReferenceId` matches the calling user.
- **Exploit:** User A completes a Stripe checkout. User B calls `POST /purchases` with User A's `StripeSessionId` and a different `TrackId`. Session status is "paid" so the check passes — User B gets a purchase record without paying.
- **Fix:** After retrieving the Stripe session, verify `ClientReferenceId` matches the calling userId, and verify trackId/licenseType from the session match the request.

### C3. Creator Email Leaked as "Artist" in Public API Responses
- **Severity:** CRITICAL
- **Files:**
  - `src/Cambrian.Application/Services/CatalogService.cs:139`
  - `src/Cambrian.Application/Services/StorefrontService.cs:135`
  - `src/Cambrian.Application/Services/StreamService.cs:25`
  - `src/Cambrian.Application/Services/PurchaseService.cs:103`
- **Issue:** When a creator has no `DisplayName`, code falls back to `t.Creator?.Email`, exposing the email in public, unauthenticated endpoints (`/discover`, `/catalog`, `/trending`, `/tracks/{id}`).
- **Exploit:** Any anonymous user can harvest creator email addresses from the public catalog.
- **Fix:** Replace `t.Creator?.Email` fallback with `"Unknown"` in all 4 services.

### C4. Hardcoded Demo User Password Seeded in Production
- **Severity:** CRITICAL
- **File:** `src/Cambrian.Api/StartupExtensions.cs:387`
- **Issue:** `const string defaultPassword = "Cambrian2026!";` for 10 demo creator accounts (aiden, bellanova, cassius, dahlia, ezra, faye, griffin, harper, indigo, juniper `@cambrianmusic.com`). Seeded in all environments except Testing — including Production.
- **Exploit:** Anyone reading the public source code can log into 10 creator accounts in production, upload content, request payouts, or impersonate creators.
- **Fix:** Guard with `if (app.Environment.IsProduction()) return;`. Use environment-variable passwords if needed elsewhere.

### C5. Stripe Session Replay Across Tracks (No Session-ID Dedup)
- **Severity:** CRITICAL
- **File:** `src/Cambrian.Application/Services/PurchaseService.cs:42-94`
- **Issue:** Duplicate check (line 61-62) only verifies `buyerId + trackId`, not `StripeSessionId`. A single paid session can be replayed for a different track.
- **Exploit:** User pays for Track A, then calls `POST /purchases` with the same `StripeSessionId` but `TrackId = Track B`. Session is paid, duplicate check passes (different track), user gets Track B for free.
- **Fix:** Query for existing purchases by `StripeSessionId` and reject if already used (as `CheckoutService.ConfirmAsync` already does at line 164).

### C6. Fake CSRF Token Endpoint (Security Theater)
- **Severity:** CRITICAL
- **File:** `src/Cambrian.Api/Controllers/AuthController.cs:119-123`
- **Issue:** `GET /auth/csrf-token` returns `Guid.NewGuid()` — never stored, never validated. Combined with `AllowCredentials()` in CORS, this provides zero CSRF protection while implying it exists.
- **Fix:** Remove this misleading endpoint. JWT Bearer auth mitigates CSRF inherently. If CSRF is needed, implement ASP.NET Core's `IAntiforgery` properly.

---

## HIGH Vulnerabilities (11)

### H1. No JWT Token Revocation — Logout is a No-Op
- **Severity:** HIGH
- **File:** `src/Cambrian.Api/Controllers/AuthController.cs:107-111`, `src/Cambrian.Application/Services/AuthService.cs:332`
- **Issue:** `POST /auth/logout` returns a success message without invalidating the token. JWTs live for 24 hours with no server-side blacklist. Admin suspension of a user has no effect on active tokens.
- **Fix:** Implement a token blacklist (Redis/DB) or switch to short-lived access tokens (5-15 min) with refresh token rotation.

### H2. Password Reset Code Brute-Forceable (6 Digits, 15 Min Window)
- **Severity:** HIGH
- **File:** `src/Cambrian.Application/Services/AuthService.cs:161`
- **Issue:** 6-digit numeric code (900K possibilities), 15-minute window. Auth rate limit allows 10/min/IP, but distributed attack with 100+ IPs yields ~15K guesses in 15 min (~1.7% success per attempt).
- **Fix:** Add per-account attempt limiting (lock after 5 failures), increase to 8+ character alphanumeric codes, or switch to cryptographic URL tokens.

### H3. Wildcard CORS with Credential Sharing
- **Severity:** HIGH
- **File:** `src/Cambrian.Api/StartupExtensions.cs:176-186`
- **Issue:** CORS uses `host.Contains(slug)` matching with `AllowCredentials()`. An attacker creating `evil-cambrian-app.vercel.app` passes the check.
- **Fix:** Use exact prefix matching: `host.StartsWith(slug + "-")` or `host == slug + ".vercel.app"`.

### H4. Anonymous Payment Result Endpoint Leaks Purchase Data
- **Severity:** HIGH
- **File:** `src/Cambrian.Api/Controllers/PaymentsController.cs:41-46`
- **Issue:** `GET /payments/result` is `[AllowAnonymous]`, returns purchase status, purchase ID, and duplicate info for any track.
- **Fix:** Require authentication and scope results to the authenticated user.

### H5. Wallet Credits Not Clawed Back on Refund/Dispute
- **Severity:** HIGH
- **File:** `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs:623-672, 677-719`
- **Issue:** Refund/dispute handlers update purchase status and remove library items, but never reverse the creator's wallet credit.
- **Exploit:** Buyer purchases → creator credited → buyer disputes → buyer refunded by Stripe → creator keeps earnings → platform loses money.
- **Fix:** Add a debit transaction to the creator's wallet matching the original credit when handling refunds/disputes.

### H6. Stripe Connect Account ID Exposed to Client
- **Severity:** HIGH
- **File:** `src/Cambrian.Application/Interfaces/ICreatorConnectService.cs:36-42`
- **Issue:** `AccountId` (Stripe `acct_xxx`) returned to the browser. Should be server-side only.
- **Fix:** Remove `AccountId` from `CreatorConnectStatusResponse`.

### H7. No Magic-Byte Validation on File Uploads
- **Severity:** HIGH
- **File:** `src/Cambrian.Application/Services/UploadService.cs:80-88, 116-124`
- **Issue:** All uploads validate only by file extension and client-provided Content-Type — both trivially spoofed. No actual file content validation.
- **Exploit:** Rename `malware.exe` to `malware.mp3`, set `Content-Type: audio/mpeg` — passes all validation. Polyglot HTML/SVG files could enable stored XSS.
- **Fix:** Read first 16-64 bytes and validate magic byte signatures. Use a library like `MimeDetective`.

### H8. Empty Content-Type Bypasses MIME Validation
- **Severity:** HIGH
- **File:** `src/Cambrian.Application/Services/UploadService.cs:85-88`
- **Issue:** MIME check: `if (!string.IsNullOrEmpty(contentType) && !AllowedMimeTypes.Contains(contentType))` — empty Content-Type skips validation entirely.
- **Fix:** Change to `if (string.IsNullOrEmpty(contentType) || !AllowedMimeTypes.Contains(contentType))`.

### H9. Hardcoded Fee Rate Contradicts Tier-Based System
- **Severity:** HIGH
- **File:** `src/Cambrian.Application/Services/PayoutService.cs:37`
- **Issue:** Hardcoded `PlatformFeeRate = 0.15m` while checkout uses tier-based rates from `TierManifest`. Earnings calculations will be incorrect for non-default tiers.
- **Fix:** Replace with `TierManifest.For(user.CreatorTier).FeeRate`.

### H10. Swagger/OpenAPI Exposed in Staging
- **Severity:** HIGH
- **File:** `src/Cambrian.Api/Program.cs:182-186`
- **Issue:** Swagger UI enabled for both Development and Staging. Staging is internet-accessible, exposing full API schema.
- **Fix:** Restrict to Development only or add authentication for Swagger in Staging.

### H11. Email/Password Reset Codes Logged in Plaintext
- **Severity:** HIGH
- **Files:**
  - `src/Cambrian.Api/Controllers/AuthController.cs:34-71` (emails in logs)
  - `src/Cambrian.Infrastructure/Email/ConsoleEmailService.cs:28-34` (reset codes in logs)
  - `src/Cambrian.Infrastructure/Email/ResendEmailService.cs:34,60`
  - `src/Cambrian.Infrastructure/Email/SmtpEmailService.cs:27,52`
- **Fix:** Hash/mask emails in logs. Ensure ConsoleEmailService is Development-only. Reduce log level for email services.

---

## MEDIUM Vulnerabilities (15)

### M1. Anonymous Audio Streaming — Full File Access Without Purchase
- **File:** `src/Cambrian.Api/Controllers/StreamController.cs:75-90`
- **Issue:** `[AllowAnonymous]` redirects to a pre-signed S3/R2 URL (1-hour expiry) for any track. No rate limiting.
- **Fix:** Require auth or serve only a 30-second preview for anonymous users.

### M2. Non-Atomic Copyright Buyout (Race Condition)
- **File:** `src/Cambrian.Application/Services/CheckoutService.cs:261-277`
- **Issue:** Copyright buyout uses read-modify-write instead of atomic CAS. Two concurrent buyouts could both succeed.
- **Fix:** Use atomic SQL UPDATE with WHERE clause, similar to `TryMarkExclusiveSoldAsync`.

### M3. Unvalidated LicenseType on PaymentCheckoutRequest
- **File:** `src/Cambrian.Application/DTOs/Payments/PaymentCheckoutRequest.cs`
- **Issue:** No `[RegularExpression]` validation (unlike `CheckoutRequest`). Unknown license types fall to default switch branch which may select a lower price.
- **Fix:** Add `[RegularExpression("^(standard|non-exclusive|exclusive|copyright_buyout)$")]`.

### M4. Missing Input Validation on Multiple DTOs
- **Files:** `UpsertCreatorProfileRequest.cs`, `SocialLinkDto.cs`, `UpsertCollectionRequest.cs`, `PurchaseCreateRequest.cs`, `ForgotPasswordRequest.cs`, `PayoutRequest.cs`, `WithdrawRequest.cs`
- **Issue:** No `[Required]`, `[MaxLength]`, `[Range]`, or `[Url]` attributes. Negative amounts, unlimited-length strings, `javascript:` URLs accepted.
- **Fix:** Add validation attributes matching DB constraints.

### M5. Stored XSS Potential in User Text Fields
- **Issue:** Bio, DisplayName, SocialLink URLs, Collection titles stored without HTML sanitization.
- **Fix:** Strip HTML tags on input or enforce frontend escaping discipline.

### M6. Missing Rate Limiting on Financial/Upload Endpoints
- **Endpoints:** `POST /checkout`, `POST /wallet/withdraw`, `POST /billing/checkout`, `POST /upload`, `GET /stream/{trackId}/audio`
- **Fix:** Apply rate-limiting policies to all financial and upload endpoints.

### M7. Self-Registration as Creator (No Verification)
- **File:** `src/Cambrian.Application/DTOs/Auth/RegisterRequest.cs:21`
- **Issue:** Any user can register with `Role = "creator"` — no email verification, no admin approval.
- **Fix:** Require email verification before enabling creator features.

### M8. Exception Messages Leak Internal Details
- **File:** `src/Cambrian.Api/Middleware/ExceptionMiddleware.cs:52-53`
- **Issue:** Raw `ex.Message` returned in non-production environments including Staging.
- **Fix:** Restrict to Development only.

### M9. Internal User IDs Exposed in Public DTOs
- **Files:** `CreatorProfileDto.cs:6` (UserId), `TrackResponse.cs:66` (CopyrightOwnerId), `TrackResponse.cs:76` (CreatorId)
- **Fix:** Remove internal GUIDs from public responses; use slugs instead.

### M10. Username/Email Enumeration via ChangeEmail
- **File:** `src/Cambrian.Application/Services/AuthService.cs:252`
- **Issue:** Returns `"Email is already in use."` — confirms email registration status.
- **Fix:** Use generic error message.

### M11. Content-Disposition Header Injection
- **File:** `src/Cambrian.Infrastructure/Storage/S3ObjectStorage.cs:89`
- **Issue:** Filename from user-controlled `track.Title` injected into Content-Disposition without escaping quotes.
- **Fix:** Use RFC 6266 `filename*=UTF-8''` encoding or strip quotes/backslashes.

### M12. Path Traversal in LocalObjectStorage
- **File:** `src/Cambrian.Infrastructure/Storage/LocalObjectStorage.cs:30`
- **Issue:** No path canonicalization guard — `..` in key could escape base directory (dev/staging only).
- **Fix:** Verify `Path.GetFullPath(filePath).StartsWith(Path.GetFullPath(_basePath))`.

### M13. Admin Temporary Password Returned in Response Body
- **File:** `src/Cambrian.Api/Controllers/AdminController.cs:168`
- **Fix:** Send via email instead.

### M14. Signed URL Expiry Mismatch (15 min advertised, 60 min actual)
- **File:** `src/Cambrian.Infrastructure/Storage/S3ObjectStorage.cs:68`
- **Fix:** Align to 15 minutes.

### M15. Auto-Migration Runs in Production (Contrary to Documentation)
- **File:** `src/Cambrian.Api/StartupExtensions.cs:189-222`
- **Issue:** `MigrateAsync()` runs in all non-Testing environments. Documentation says migrations are "logged but NOT applied" in production.
- **Fix:** Add production guard.

---

## LOW Vulnerabilities (10)

| # | Issue | File |
|---|-------|------|
| L1 | No account lockout after failed logins | `AuthService.cs` (Identity config) |
| L2 | Stream stop has no ownership validation | `StreamController.cs:116-125` |
| L3 | `CreditCreator` endpoint is a no-op returning success | `PurchaseService.cs:158-163` |
| L4 | No minimum withdrawal amount | `WalletService.cs:40-55` |
| L5 | `PaymentService.GetStateAsync` returns hardcoded "ready" | `PaymentService.cs:57-66` |
| L6 | R2ObjectStorage stub returns fake "signed" URLs | `R2ObjectStorage.cs:17-21` |
| L7 | `Console.WriteLine` logging of S3 keys/paths | `S3ObjectStorage.cs:42,55,110,121` |
| L8 | Missing `Content-Security-Policy` header | `Program.cs` |
| L9 | `AllowedHosts: *` in base appsettings | `appsettings.json:50` |
| L10 | Endpoint manifest drift (duplicates, missing role info) | `contracts/endpoint-manifest.v1.json` |

---

## Positive Security Patterns

The codebase demonstrates several strong practices:

1. **Prices always fetched from DB** — all checkout/purchase flows resolve from Track entity, never client
2. **Entity Framework parameterized queries** — no SQL injection vectors found
3. **Stripe webhook signature verification** in non-dev environments
4. **Atomic exclusive sale guards** — `TryMarkExclusiveSoldAsync` uses SQL-level CAS
5. **Atomic wallet withdrawals** — serializable isolation transactions
6. **Non-root Docker container** (appuser, UID 1001)
7. **Comprehensive security headers** (HSTS, X-Frame-Options, X-Content-Type-Options, etc.)
8. **Rate limiting on auth endpoints** with per-IP partitioning
9. **Startup guards** crash the app if critical secrets are misconfigured in production
10. **Download entitlement checks** verify purchase ownership before serving files
11. **Password hashing** via ASP.NET Identity (bcrypt)
12. **Password policy** enforces 8+ chars with complexity requirements
13. **Reset codes hashed** with SHA-256 before DB storage
14. **Static file middleware** blocks direct audio file access

---

## Priority Remediation Order

### Immediate (CRITICAL — Financial/Data Impact)
1. **C1** — Payout race condition (direct financial loss)
2. **C2** — Stripe session ownership bypass (free purchases)
3. **C5** — Stripe session replay across tracks (free purchases)
4. **C3** — Email leaked in public catalog (privacy violation)
5. **C4** — Demo credentials in production (account takeover)
6. **H5** — No wallet clawback on refund/dispute (platform financial loss)

### Urgent (HIGH — Security Gaps)
7. **H1** — Token revocation / shorter JWT lifetime
8. **H7+H8** — File upload magic byte + MIME validation
9. **H3** — CORS wildcard tightening
10. **H4** — Anonymous payment result endpoint
11. **H9** — Fee rate consistency
12. **H11** — Sensitive data in logs

### Important (MEDIUM — Hardening)
13. **M1** — Anonymous streaming protection
14. **M3+M4** — DTO input validation
15. **M6** — Rate limiting on financial endpoints
16. **M2** — Atomic copyright buyout

---

## Dependencies & Supply Chain

All NuGet packages are current with no known CVEs:

| Package | Version | Status |
|---------|---------|--------|
| Microsoft.EntityFrameworkCore | 8.0.12 | Current |
| Stripe.net | 46.2.0 | Current |
| AWSSDK.S3 | 3.7.305 | Current |
| BCrypt.Net-Next | 4.0.3 | Current |
| MailKit | 4.9.0 | Current |
| QuestPDF | 2026.2.3 | Current |
| Npgsql.EF.PostgreSQL | 8.0.11 | Current |

**.NET 8 LTS** support ends November 2026 — plan migration to .NET 10 LTS.

---

## Success Criteria Assessment

| Criterion | Status | Notes |
|-----------|--------|-------|
| No unauthorized access possible | **FAIL** | C2, C5 allow free purchases; C4 allows demo account takeover |
| No creator identity spoofing | **PASS** | UUID is the relational key; username not used as FK |
| No sensitive data exposed | **FAIL** | C3 leaks email; H6 leaks Stripe IDs; M9 leaks internal GUIDs |
| Uploads fully validated | **FAIL** | H7, H8 — no magic byte or strict MIME validation |
| Payments cannot be manipulated | **FAIL** | C1 payout race condition; C2/C5 session hijack/replay; H5 no refund clawback |

**Overall Assessment: The platform requires remediation of CRITICAL and HIGH issues before it can be considered production-safe for handling real payments and user data.**
