# Backend Audit Report — Cambrian API

**Date:** 2026-03-19  
**Auditor:** Senior Backend Auditor (Principal-level)  
**Scope:** Backend only — services, controllers, DB layer, auth, payments, storage, integrations  
**Mode:** Audit only — no changes implemented

---

## 1. Documents Read

| Document | Status | Notes |
|----------|--------|-------|
| `GOVERNANCE.md` | **MISSING** | No governance document exists |
| `ARCHITECTURE.md` | **MISSING** | Architecture is documented in `README.md` only |
| `API_CONTRACTS.md` | **MISSING** | Contracts live in `contracts/openapi.v1.json` (read in full, ~2600 lines) |
| `DOMAIN_MODELS.md` / `DATA_MODELS` | **MISSING** | Entities must be inferred from source code |
| `SECURITY.md` | **MISSING** | Security posture documented partially in `README.md` and `DEPLOYMENT.md` |
| `DATABASE.md` / Migrations docs | **MISSING** | Migrations exist in code only (`src/Cambrian.Persistence/Migrations/`) |
| `TESTING.md` | **MISSING** | Testing documented in `README.md` only |
| `contracts/openapi.v1.json` | **READ** | Full OpenAPI 3.0.1 spec — source of truth for API shapes |
| `contracts/endpoint-manifest.v1.json` | **READ** | 107 endpoints with auth flags — source of truth for auth requirements |
| `contracts/policy.v1.json` | **READ** | 7 governance rules — source of truth for compliance policy |
| `README.md` | **READ** | Architecture overview, endpoint table, roles, rate limiting |
| `DEPLOYMENT.md` | **READ** | Environment variables, startup guards, deployment checklist |

**Governance Gap:** 7 of 7 mandatory governance documents are missing. The only available sources of truth are the OpenAPI spec, endpoint manifest, and policy.v1.json. This is itself a **HIGH severity finding** — without codified governance, there is no enforceable baseline for audits.

---

## 2. Backend Health Summary

| Dimension | Assessment |
|-----------|------------|
| **Overall Health** | DEGRADED — functional but with exploitable vulnerabilities |
| **Production Risk Level** | **HIGH** — 6 critical + 10 high-severity issues found |
| **API Contract Compliance** | PARTIAL — 8 auth mismatches between manifest and code |
| **Payment Safety** | AT RISK — dual-fulfillment race, legacy bypass endpoint, missing refund handling |
| **Data Integrity** | MODERATE — missing FKs, full-table scans, non-atomic multi-step writes |

### Top 5 Issues

1. **CRIT-01** — `PaymentService.ProcessAsync` marks purchases as "completed" without Stripe verification
2. **CRIT-02** — `CreatorTier` never upgraded when user subscribes to Pro — all Pro creators charged 35% instead of 15%
3. **CRIT-03** — `PayoutController` has no creator-role enforcement, violating policy `payout-routes-require-creator-role`
4. **CRIT-04** — `GET /stream/{trackId}/audio` is `[AllowAnonymous]` but manifest declares `requiresAuth: true`
5. **CRIT-05** — `CheckoutService.ConfirmAsync` and `StripeWebhookService` both fulfill purchases concurrently with no shared lock

---

## 3. Critical Findings

### CRIT-01: Payment Bypass via Legacy Endpoint

| Field | Detail |
|-------|--------|
| **Severity** | CRITICAL |
| **File(s)** | `src/Cambrian.Application/Services/PaymentService.cs:84-98` |
| **Issue** | `ProcessAsync()` marks any purchase as "completed" based solely on the caller's JWT, without calling Stripe to verify payment was received |
| **Why it matters** | Any authenticated user can call `POST /payments/process` with a purchase ID and claim a track for free |
| **Contract/Policy violated** | Business rule: purchases require verified payment. OpenAPI marks `PaymentProcessRequest.purchaseId` as required but has no payment verification semantics |
| **Recommended fix** | Either (a) remove this endpoint entirely, or (b) add `_gateway.GetCheckoutSessionAsync()` verification before marking completed, mirroring `PurchaseService.CreateAsync` which already does this correctly |
| **Breaking?** | No — the endpoint currently has no legitimate use case |

### CRIT-02: CreatorTier Never Upgraded on Subscription

| Field | Detail |
|-------|--------|
| **Severity** | CRITICAL |
| **File(s)** | `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs:475-511` |
| **Issue** | `HandleSubscriptionCheckout()` sets `user.Tier = tier` (string) but never sets `user.CreatorTier = CreatorTier.Pro` (enum). `TierManifest.For(user.CreatorTier)` always returns `Free` config |
| **Why it matters** | Pro subscribers pay 35% platform fee instead of 15%. Upload limits incorrect. Revenue dashboards wrong |
| **Contract/Policy violated** | `TierManifest` is defined as single source of truth for fees/limits. The `CreatorTier` enum is the input to `TierManifest.For()` — bypassing it breaks the entire tier system |
| **Recommended fix** | Add `user.CreatorTier = tier == "pro" ? CreatorTier.Pro : CreatorTier.Free;` in `HandleSubscriptionCheckout`. Also add `user.SubscriptionStatus = "Active";` |
| **Breaking?** | No |

### CRIT-03: Payout Routes Missing Creator-Role Enforcement

| Field | Detail |
|-------|--------|
| **Severity** | CRITICAL |
| **File(s)** | `src/Cambrian.Api/Controllers/PayoutController.cs:9-10` |
| **Issue** | Controller uses `[Authorize]` (generic auth) only. All 13 payout endpoints are accessible to any authenticated user, not just creators |
| **Why it matters** | Non-creator users can access payout endpoints, connect Stripe accounts, view earnings, and potentially request payouts |
| **Contract/Policy violated** | `contracts/policy.v1.json` rule `payout-routes-require-creator-role`: "Payout endpoints require creator role" |
| **Recommended fix** | Add `[RequireCreatorTier]` filter or `[Authorize(Roles = "Creator")]` to the controller class. `CreatorController` already demonstrates the correct pattern |
| **Breaking?** | Yes for non-creator users who might be incorrectly using these endpoints |

### CRIT-04: Stream Audio Endpoint Auth Mismatch

| Field | Detail |
|-------|--------|
| **Severity** | CRITICAL |
| **File(s)** | `src/Cambrian.Api/Controllers/StreamController.cs:75-90` |
| **Issue** | `GET /stream/{trackId}/audio` uses `[AllowAnonymous]` but the endpoint manifest declares `requiresAuth: true`. Full-quality audio is accessible without any authentication |
| **Why it matters** | The entire catalog's audio is accessible to anonymous users. This effectively gives away the product for free |
| **Contract/Policy violated** | `contracts/endpoint-manifest.v1.json` entry for `/stream/{trackId}/audio` has `requiresAuth: true`. Also violates `contracts/policy.v1.json` rule `api-contract-cannot-be-broken` |
| **Recommended fix** | Either (a) change to `[Authorize]` to match the manifest, or (b) implement preview truncation (30s clip) for anonymous access and update the manifest to `requiresAuth: false` with documentation |
| **Breaking?** | Yes if frontend relies on anonymous audio playback |

### CRIT-05: Dual-Fulfillment Race Condition

| Field | Detail |
|-------|--------|
| **Severity** | CRITICAL |
| **File(s)** | `src/Cambrian.Application/Services/CheckoutService.cs:112-338`, `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs:266-468` |
| **Issue** | Both `CheckoutService.ConfirmAsync` (called by user polling) and `StripeWebhookService.HandleCheckoutCompleted` (called by Stripe webhook) can fulfill the same payment simultaneously. No shared lock, no unique constraint on (BuyerId, TrackId, LicenseType, StripeSessionId) |
| **Why it matters** | Creator wallet credited twice for the same purchase. Possible duplicate library items |
| **Contract/Policy violated** | Payment integrity — no explicit policy document, but the `StripeWebhookEvents` idempotency ledger only covers the webhook path, not the ConfirmAsync path |
| **Recommended fix** | (a) Add a unique DB constraint on `Purchases(BuyerId, TrackId, LicenseType, StripeSessionId)`, and/or (b) have `ConfirmAsync` check the `StripeWebhookEvents` ledger before fulfilling, and/or (c) use a DB advisory lock on `stripeSessionId` |
| **Breaking?** | No |

### CRIT-06: Non-Atomic Copyright Buyout

| Field | Detail |
|-------|--------|
| **Severity** | CRITICAL |
| **File(s)** | `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs:302-316` |
| **Issue** | Copyright buyout uses read-then-write (`if (track.ExclusiveSold)` → `track.ExclusiveSold = true`). The exclusive license path correctly uses `ExecuteSqlInterpolatedAsync` (line 292) but copyright buyout does not |
| **Why it matters** | Two concurrent copyright buyout requests could both succeed, transferring copyright to two different buyers |
| **Contract/Policy violated** | Data integrity — exclusive ownership must be atomic |
| **Recommended fix** | Use the same `ExecuteSqlInterpolatedAsync` atomic CAS pattern used at line 292 |
| **Breaking?** | No |

### HIGH-01: Webhook Secret Not Validated at Startup

| Field | Detail |
|-------|--------|
| **Severity** | HIGH |
| **File(s)** | `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs:33`, `src/Cambrian.Api/StartupExtensions.cs:256-272` |
| **Issue** | `_webhookSecret = configuration["Stripe:WebhookSecret"] ?? ""` — empty string silently disables signature verification. `StartupExtensions.ValidateStripeKey` validates the SecretKey but not the WebhookSecret |
| **Why it matters** | In production, if `Stripe:WebhookSecret` is accidentally omitted, any HTTP client can send fake webhook events |
| **Contract/Policy violated** | Security policy — webhook endpoints must verify signatures |
| **Recommended fix** | Add validation in `ValidateStripeKey`: `if (isProduction && string.IsNullOrWhiteSpace(webhookSecret)) throw` |
| **Breaking?** | No |

### HIGH-02: CheckoutService Lacks Transaction

| Field | Detail |
|-------|--------|
| **Severity** | HIGH |
| **File(s)** | `src/Cambrian.Application/Services/CheckoutService.cs:112-338` |
| **Issue** | `ConfirmAsync` performs 5+ sequential DB operations (purchase insert, track update, library insert, wallet credit, license link) without a wrapping transaction |
| **Why it matters** | If any operation fails mid-sequence, the user can end up with a purchase but no library access, or a wallet credit but no purchase record |
| **Contract/Policy violated** | Data integrity — ACID compliance |
| **Recommended fix** | Wrap the entire fulfillment in `using var tx = await _db.Database.BeginTransactionAsync()` — the webhook service already uses this pattern correctly |
| **Breaking?** | No |

### HIGH-03: ExceptionMiddleware Returns 403 for Auth Failures

| Field | Detail |
|-------|--------|
| **Severity** | HIGH |
| **File(s)** | `src/Cambrian.Api/Middleware/ExceptionMiddleware.cs:37` |
| **Issue** | `UnauthorizedAccessException` → HTTP 403 Forbidden. Should be 401 Unauthorized |
| **Why it matters** | Frontend interprets 403 as "you don't have permission" (permanent) vs 401 "please authenticate" (recoverable). Login failures return wrong semantics |
| **Contract/Policy violated** | HTTP specification (RFC 7235 §3.1) |
| **Recommended fix** | Change to `(int)HttpStatusCode.Unauthorized` |
| **Breaking?** | Potentially — frontend error handlers may be keying on 403 |

### HIGH-04: GetSessionAsync Returns Empty Token

| Field | Detail |
|-------|--------|
| **Severity** | HIGH |
| **File(s)** | `src/Cambrian.Application/Services/AuthService.cs:125-138` |
| **Issue** | `Token = ""` and `Role` is not set in the session response DTO |
| **Why it matters** | Frontend session restoration after page refresh fails — user appears logged out. The OpenAPI schema declares `token` as required in `AuthSession` |
| **Contract/Policy violated** | `contracts/openapi.v1.json` schema `AuthSession` requires `token: string` (non-empty implied) |
| **Recommended fix** | Call `GenerateJwt(user)` to produce a fresh token; include `Role = user.Role` |
| **Breaking?** | No — this fixes broken behavior |

### HIGH-05: PayoutService Hardcodes 15% Fee Rate

| Field | Detail |
|-------|--------|
| **Severity** | HIGH |
| **File(s)** | `src/Cambrian.Application/Services/PayoutService.cs:37` |
| **Issue** | `const decimal PlatformFeeRate = 0.15m` — hardcoded. The rest of the system uses `TierManifest.For(user.CreatorTier).FeeRate` which returns 0.35 for Free, 0.15 for Pro |
| **Why it matters** | Free-tier creators see 15% fee in earnings dashboard but are actually charged 35% on purchases. Revenue mismatch |
| **Contract/Policy violated** | `TierManifest` is documented as "single source of truth for creator tier rules" |
| **Recommended fix** | Look up the creator's `CreatorTier` and use `TierManifest.For(tier).FeeRate` |
| **Breaking?** | No — corrects incorrect behavior |

### HIGH-06: PurchaseService.CreateAsync Lacks Transaction

| Field | Detail |
|-------|--------|
| **Severity** | HIGH |
| **File(s)** | `src/Cambrian.Application/Services/PurchaseService.cs:82-130` |
| **Issue** | 4 sequential writes (purchase, library, invoice, license) without a transaction. If `InvoiceRepo.AddAsync` fails, a purchase exists with library access but no invoice |
| **Why it matters** | Financial record inconsistency — purchase exists but no invoice for accounting/audit |
| **Recommended fix** | Wrap in a DB transaction |
| **Breaking?** | No |

### HIGH-07: ExceptionMiddleware Missing HasStarted Guard

| Field | Detail |
|-------|--------|
| **Severity** | HIGH |
| **File(s)** | `src/Cambrian.Api/Middleware/ExceptionMiddleware.cs:34` |
| **Issue** | Sets `Response.StatusCode` and writes to `Response.Body` without checking `context.Response.HasStarted` |
| **Why it matters** | Streaming endpoints (audio redirect) that throw mid-response will crash with `InvalidOperationException` |
| **Recommended fix** | Add `if (context.Response.HasStarted) { _logger.LogError(...); return; }` before line 34 |
| **Breaking?** | No |

### HIGH-08: Dockerfile Shell-Form ENTRYPOINT

| Field | Detail |
|-------|--------|
| **Severity** | HIGH |
| **File(s)** | `Dockerfile:25` |
| **Issue** | `ENTRYPOINT sh -c "..."` prevents `SIGTERM` from reaching .NET process |
| **Why it matters** | Non-graceful shutdown on container restart — in-flight DB connections, HTTP requests dropped |
| **Recommended fix** | Change to `ENTRYPOINT ["sh", "-c", "exec ASPNETCORE_URLS=http://+:$PORT dotnet Cambrian.Api.dll"]` |
| **Breaking?** | No |

### HIGH-09: Missing Webhook Handlers for Refunds/Disputes

| Field | Detail |
|-------|--------|
| **Severity** | HIGH |
| **File(s)** | `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs` |
| **Issue** | No handlers for `charge.refunded`, `charge.dispute.created`, `checkout.session.expired` |
| **Why it matters** | Refunded purchases keep library/download access. Disputes go unnoticed. Expired sessions leave dangling state |
| **Recommended fix** | Add handlers to revoke access on refund/dispute |
| **Breaking?** | No |

### HIGH-10: CheckoutService Non-Atomic Exclusive Check

| Field | Detail |
|-------|--------|
| **Severity** | HIGH |
| **File(s)** | `src/Cambrian.Application/Services/CheckoutService.cs:226-231` |
| **Issue** | Uses `if (!track.ExclusiveSold) { track.ExclusiveSold = true; await _tracks.UpdateAsync(track); }` — not atomic. `PurchaseService` correctly uses `TryMarkExclusiveSoldAsync` |
| **Recommended fix** | Use `ExecuteSqlInterpolatedAsync` or the existing `TryMarkExclusiveSoldAsync` method |
| **Breaking?** | No |

---

## 4. API Contract Compliance

### Endpoint Manifest vs Code Auth Mismatches

| Endpoint | Manifest `requiresAuth` | Code Auth | Violation |
|----------|------------------------|-----------|-----------|
| `GET /discover` | `true` | None (no `[Authorize]`) | **MISMATCH** |
| `GET /catalog` | `true` | None | **MISMATCH** |
| `GET /trending` | `true` | None | **MISMATCH** |
| `GET /tracks/{trackId}` | `true` | None | **MISMATCH** |
| `GET /stream/{trackId}/audio` | `true` | `[AllowAnonymous]` | **MISMATCH** |
| `GET /subscriptions/plans` | `true` | `[AllowAnonymous]` | **MISMATCH** |
| `GET /community` | `true` | None | **MISMATCH** |
| `GET /tracks` | `true` | None | **MISMATCH** |

**Note:** The `README.md` documents several of these as not requiring auth (catalog, tracks, discover). This means the manifest is out of date, not the code. However, per policy rule `manifest-must-match-openapi`, the manifest must match — whichever direction the fix goes, they must be synchronized.

### Undocumented Endpoints (in code, not in manifest)

| Route | Controller | Risk |
|-------|------------|------|
| `POST /admin/purge-test-data` | AdminController | **HIGH** — destructive operation, undocumented |
| `GET /debug/user/{userId}` | DebugController | **MEDIUM** — exposes user data |
| `GET /debug/webhooks` | DebugController | **MEDIUM** — exposes webhook payloads |
| `GET /debug/consistency` | DebugController | **LOW** |
| `GET /download/{trackId}/file` | DownloadController | **LOW** |
| `GET /licenses/{licenseId}/pdf` | LicensesController | **LOW** |

### DTO Shape Issues

| Issue | Count | Examples |
|-------|-------|---------|
| `AuthResponse.UserId` is `Guid` but `ApplicationUser.Id` is `string` | 1 | Throws `FormatException` if Identity generates non-GUID ID |
| Entity fields not exposed in any DTO | 28 | `Track.Mood`, `Track.Tempo`, `Track.Instrumental`, `Track.Tags`, `Purchase.ExpiresAt` |
| Missing validation annotations on financial DTOs | 5+ | `PayoutRequest.Amount`, `WithdrawRequest.Amount`, `CreditCreatorRequest.Amount` accept negative values |
| `PayoutResponse` too sparse | 1 | Returns only `Amount`/`Status`, missing `Id`, `RequestedAt`, `CompletedAt` |

---

## 5. Authentication / Authorization

### User Identity

| Check | Status | Detail |
|-------|--------|--------|
| Identity field | Email | Login/register use email. `UserName` is set to email |
| JWT claims | OK | `sub` = userId, `email`, `role`, `tier`, `jti` |
| Token expiry | OK | 24 hours |
| Token refresh | **BROKEN** | `GetSessionAsync` returns `Token = ""` (see HIGH-04) |
| JWT key validation | OK | Fails fast if < 32 chars or missing |

### Role Enforcement

| Check | Status | Detail |
|-------|--------|--------|
| Admin routes | OK | `[Authorize(Roles = "Admin")]` on AdminController |
| Creator routes | PARTIAL | `CreatorController` uses `[RequireCreatorTier]` correctly; `PayoutController` does NOT (see CRIT-03) |
| Payout policy | **VIOLATED** | `policy.v1.json` requires creator role on `/payouts/*` |
| Feature flags write | OK | `[Authorize(Roles = "Admin")]` on PUT/DELETE |
| Purchases credit-creator | **MISSING** | `POST /purchases/credit-creator` requires only `[Authorize]` but should be Admin-only per OpenAPI tag |

### Access Leaks

| Issue | Severity |
|-------|----------|
| 48 null-forgiving operators (`!`) on `User.FindFirstValue()` across 11 controllers | Medium — NullReferenceException on malformed JWT |
| `CatalogController` has no `[Authorize]` at all — 5 endpoints open to anonymous | Medium (intentional per README, but conflicts with manifest) |
| `GET /stream/{trackId}/audio` allows anonymous full-quality streaming | Critical |
| `Debug` endpoints accessible in non-production without Admin role check | Medium |

---

## 6. Payments Audit (Stripe)

### Webhook Handling

| Check | Status | Detail |
|-------|--------|--------|
| Signature verification | CONDITIONAL | Skipped when `_webhookSecret` is empty string (HIGH-01) |
| Idempotency | OK | `StripeWebhookEvents` ledger with unique index on `EventId` |
| Transaction wrapping | OK | Webhook handler uses `BeginTransactionAsync` with rollback |
| Event types handled | PARTIAL | `checkout.session.completed`, `customer.subscription.deleted`, `invoice.payment_failed` |
| Missing event types | HIGH | `charge.refunded`, `charge.dispute.created`, `checkout.session.expired`, `customer.subscription.updated` |

### Double Charge / Duplicate Risk

| Check | Status | Detail |
|-------|--------|--------|
| Duplicate purchase prevention | OK | Checks `(BuyerId, TrackId, LicenseType)` before creating |
| Dual-fulfillment race | **CRITICAL** | Both webhook and ConfirmAsync can fulfill simultaneously (CRIT-05) |
| WebhookController error handling | HIGH | Returns non-2xx on processing errors → Stripe retries for 72 hours |

### Missing Verification

| Endpoint | Issue |
|----------|-------|
| `POST /payments/process` | Marks purchase completed without Stripe verification (CRIT-01) |
| `POST /purchases/credit-creator` | No-op stub — silently does nothing |
| Payout settings POST/PUT | No-op stubs returning fake success |

### Failure Handling

| Scenario | Handled? |
|----------|----------|
| Stripe transfer fails during payout | YES — wallet refunded, payout marked "failed" |
| License certificate issuance fails | YES — logged as `[LICENSE-FAILED]`, purchase still completes |
| Checkout session expired | NO — no handler |
| Charge refunded | NO — access not revoked |

---

## 7. Data Integrity + Database

### Migration Safety

| Check | Status |
|-------|--------|
| Auto-migration in production | SAFE — disabled in production, requires manual run |
| Auto-migration in non-prod | CAUTION — errors swallowed, app continues on stale schema |
| Unique constraints | GOOD — `StripeWebhookEvents.EventId`, `Track.CambrianTrackId`, `LibraryItem(UserId, TrackId)`, `Purchase.StripeSessionId` |

### Missing Constraints

| Entity | Missing |
|--------|---------|
| `CreatorProfile.UserId` | No FK relationship to `AspNetUsers` — orphans possible |
| `TrackCollection.CreatorId` | No FK relationship — orphans possible |
| `AnalyticsEvent.UserId` / `TrackId` | No FK relationships — orphans possible |
| `Payout` | Missing `StripeTransferId` column for reconciliation |
| `Subscription` | Missing `StripeSubscriptionId` column for Stripe matching |

### Transaction Coverage

| Operation | Transactional? |
|-----------|---------------|
| Webhook fulfillment (`StripeWebhookService`) | YES |
| `CheckoutService.ConfirmAsync` | **NO** (HIGH-02) |
| `PurchaseService.CreateAsync` | **NO** (HIGH-06) |
| `PayoutService.RequestAsync` | **NO** — balance check + debit is non-atomic |

### Full-Table Scans (Performance)

| Repository | Method | Impact |
|------------|--------|--------|
| `CreatorProfileRepository` | ALL methods (`GetByUserIdAsync`, `GetBySlugAsync`, `UpsertAsync`, `UpdateImageAsync`, `UpdatePinnedTracksAsync`, `GetCollectionsAsync`) | Loads entire `CreatorProfiles` table into memory |
| `StreamService.ListStreamableAsync` | `BrowseAsync()` | Loads all tracks then `.Take()` in memory |
| `CatalogService` | `FindByIdAsync` per track for creator tier | N+1 query — 50 tracks = 50 extra DB calls |

### Enum vs String Mismatch

| Field | Entity Type | Values Used | Issue |
|-------|-------------|-------------|-------|
| `ApplicationUser.Tier` | `string` | "free", "paid", "creator", "pro" | Overlaps with `CreatorTier` enum |
| `ApplicationUser.CreatorTier` | `CreatorTier` enum | `Free`, `Pro` | Never upgraded on subscription (CRIT-02) |
| `Purchase.Status` | `string` | "pending", "completed" | Should be enum |
| `Purchase.LicenseType` | `string` | "exclusive", "non-exclusive", "copyright_buyout" | `LicenseType` enum lacks "non-exclusive" |
| `Payout.Status` | `string` | "pending", "completed", "failed" | `PayoutStatus` enum has incompatible values |

---

## 8. Media / File Handling

### Audio Storage

| Check | Status | Detail |
|-------|--------|--------|
| Upload size limit | PARTIAL | `UploadService` validates 100MB but `UploadController` uses `[DisableRequestSizeLimit]` — Kestrel buffers entire body before validation |
| Extension validation | OK | Allowed: `.mp3`, `.wav`, `.flac`, `.aac`, `.ogg`, `.m4a`, `.wma` |
| MIME type validation | **BYPASSABLE** | Skipped when `Content-Type` header is empty/null (`UploadService.cs:86`) |
| Path traversal | LOW | Storage key built from `CreatorId` (from JWT) + GUID — partially mitigated |
| Orphaned files | YES | Non-atomic upload + DB write — file uploaded to storage, then DB record created |

### Image / Profile Upload

| Check | Status | Detail |
|-------|--------|--------|
| Size limit | OK | 10 MB max enforced |
| Extension validation | OK | `.jpg`, `.jpeg`, `.png`, `.webp` only |
| File overwrite | NO RISK | Keys include GUID — no collisions |

### Storage Provider Switching

| Check | Status | Detail |
|-------|--------|--------|
| Production guard | OK | Production rejects local storage |
| Silent fallback | **HIGH** | Non-production silently falls back to `LocalObjectStorage` when S3 creds incomplete. Tracks uploaded during fallback get local-path `AudioUrl`. When S3 activates, those tracks are permanently unplayable |

---

## 9. Concurrency / Race Conditions

| ID | Location | Issue | Risk |
|----|----------|-------|------|
| **RACE-01** | `CheckoutService.ConfirmAsync:226-231` | Non-atomic exclusive license check-and-set | Two buyers get exclusive license |
| **RACE-02** | `StripeWebhookService:302-316` | Non-atomic copyright buyout check-and-set | Double copyright transfer |
| **RACE-03** | `CheckoutService` + `StripeWebhookService` | Dual fulfillment of same payment | Double wallet credit |
| **RACE-04** | `PayoutService.RequestAsync:97-112` | Balance check then debit without transaction/lock | Double withdrawal if two requests overlap |
| **RACE-05** | `CreatorProfileRepository.UpsertAsync:40-78` | Full table load + foreach find + separate insert/update | Concurrent upserts could create duplicate profiles (mitigated by unique index on `UserId`) |

---

## 10. Error Handling & Logging

### Silent Failures

| Location | Issue |
|----------|-------|
| `PurchaseService.CreditCreatorAsync` | No-op stub — returns `Task.CompletedTask` silently |
| `PayoutController.CreateSettings` / `UpdateSettings` | Returns fake success without persisting anything |
| `AuthService.ForgotPasswordAsync` | Swallows email send failure (intentional for security, but no log) |
| Migration errors in non-production | Caught and printed, app continues on stale schema |

### Missing Logs

| Location | Issue |
|----------|-------|
| `S3ObjectStorage` | Uses `Console.WriteLine` instead of `ILogger` — bypasses log aggregation |
| `ExceptionMiddleware` | Non-500 errors expose `ex.Message` in production (may contain internal details) |
| `RequestLoggingMiddleware` | No try/finally — request timing lost on exceptions |

### Inconsistent Error Responses

| Issue | Detail |
|-------|--------|
| 403 vs 401 | `UnauthorizedAccessException` returns 403 instead of 401 |
| `ExceptionMiddleware` no `HasStarted` guard | Will throw if response already streaming |
| `WebhookController` returns non-2xx on errors | Causes Stripe to retry for 72 hours |

---

## 11. Testing Gaps

### Existing Test Coverage (34 test files)

| Area | Test File(s) | Status |
|------|-------------|--------|
| Auth | `AuthTests.cs`, `AuthControllerTests.cs` | COVERED |
| Checkout | `CheckoutTests.cs`, `CheckoutControllerTests.cs`, `CheckoutServiceTests.cs` | COVERED |
| Library | `LibraryTests.cs`, `LibraryControllerTests.cs` | COVERED |
| Upload | `UploadValidationTests.cs`, `UploadServiceTests.cs` | COVERED |
| Download | `DownloadTests.cs`, `DownloadControllerTests.cs` | COVERED |
| Payments | `PaymentsControllerTests.cs`, `PaymentServiceTests.cs`, `Phase2PaymentTests.cs` | COVERED |
| Webhook | `WebhookControllerTests.cs`, `WebhookEndToEndTests.cs`, `StripeWebhookServiceTests.cs` | COVERED |
| Purchases | `PurchaseJourneyTests.cs`, `PurchaseServiceTests.cs` | COVERED |
| Billing | `BillingControllerTests.cs`, `BillingTierTests.cs` | COVERED |
| Subscriptions | `SubscriptionFlowTests.cs` | COVERED |
| Security | `Phase1SecurityTests.cs` | COVERED |
| Creator | `CreatorConnectServiceTests.cs`, `CreatorProfileContractTests.cs` | COVERED |
| Copyright | `CopyrightBuyoutTests.cs` | COVERED |
| Catalog | `CatalogControllerTests.cs`, `CatalogServiceTests.cs` | COVERED |
| Storefront | `StorefrontTests.cs` | COVERED |
| Cover Art | `CoverArtUploadTests.cs` | COVERED |
| DB Consistency | `DatabaseConsistencyTests.cs` | COVERED |
| Licenses | `LicenseCertificateIntegrationTests.cs` | COVERED |

### Missing Tests

| Area | Missing Test | Why It Matters |
|------|-------------|----------------|
| Dual-fulfillment race | No test for concurrent webhook + ConfirmAsync | Critical race condition untested |
| Payout withdrawal race | No test for concurrent payout requests | Could result in double withdrawal |
| Refund/dispute handling | No tests for `charge.refunded` flow | No such flow exists to test |
| CreatorTier upgrade | No test verifying `CreatorTier` changes on subscription | CRIT-02 would be caught |
| PaymentService.ProcessAsync bypass | No test verifying payment verification | CRIT-01 would be caught |
| MIME type bypass | No test for upload with empty Content-Type | Upload validation bypassable |
| Payout role enforcement | No test that regular User cannot access payout endpoints | CRIT-03 would be caught |
| Auth manifest compliance | No automated test matching code auth to manifest auth | 8 mismatches would be caught |
| Contract schema validation | `ContractTests` not found in test listing | May have been removed |

---

## 12. Safe Fix Plan

### Phase 1: Critical Non-Breaking Fixes (Immediate)

| # | Fix | File(s) | Risk | Rollback |
|---|-----|---------|------|----------|
| 1.1 | Remove or gate `POST /payments/process` behind Admin-only or add Stripe session verification | `PaymentService.cs` | LOW — no legitimate callers | Remove `[HttpPost("process")]` route or add `[Authorize(Roles = "Admin")]` |
| 1.2 | Add `user.CreatorTier = CreatorTier.Pro` in `HandleSubscriptionCheckout` | `StripeWebhookService.cs:500-503` | LOW — adds missing assignment | Revert single line |
| 1.3 | Add `[RequireCreatorTier]` to `PayoutController` class | `PayoutController.cs:10` | MEDIUM — may break non-creator users accessing payouts | Add fallback error message |
| 1.4 | Validate `Stripe:WebhookSecret` at startup in production | `StartupExtensions.cs:256-272` | LOW — fail-fast is correct behavior | Revert validation |
| 1.5 | Use atomic CAS for copyright buyout (copy exclusive pattern) | `StripeWebhookService.cs:302-316` | LOW — follows existing pattern | Revert to original check |
| 1.6 | Return 200 from `WebhookController` even on processing errors | `WebhookController.cs:36-44` | LOW — prevents Stripe retry storm | Revert catch block |

### Phase 2: High-Priority Fixes

| # | Fix | File(s) | Risk | Rollback |
|---|-----|---------|------|----------|
| 2.1 | Fix `GetSessionAsync` to generate fresh JWT and include Role | `AuthService.cs:125-138` | LOW | Revert 2 lines |
| 2.2 | Change `UnauthorizedAccessException` → 401 in ExceptionMiddleware | `ExceptionMiddleware.cs:37` | LOW — check frontend handlers | Revert enum value |
| 2.3 | Add `Response.HasStarted` guard in ExceptionMiddleware | `ExceptionMiddleware.cs:34` | NONE | Revert guard |
| 2.4 | Wrap `CheckoutService.ConfirmAsync` in DB transaction | `CheckoutService.cs` | LOW | Revert transaction wrapper |
| 2.5 | Use `TryMarkExclusiveSoldAsync` in CheckoutService | `CheckoutService.cs:226-231` | LOW | Revert to original check |
| 2.6 | Fix `PayoutService` to use `TierManifest` fee rates | `PayoutService.cs:37` | LOW | Revert to constant |
| 2.7 | Add `exec` to Dockerfile ENTRYPOINT | `Dockerfile:25` | NONE | Revert to shell form |
| 2.8 | Synchronize endpoint manifest auth flags with code | `contracts/endpoint-manifest.v1.json` | LOW | Revert manifest |

### Phase 3: Hardening

| # | Fix | File(s) | Risk | Rollback |
|---|-----|---------|------|----------|
| 3.1 | Replace `CreatorProfileRepository` full-table scans with `FirstOrDefaultAsync(predicate)` | `CreatorProfileRepository.cs` | LOW | Revert queries |
| 3.2 | Add FK constraints for `CreatorProfile.UserId`, `TrackCollection.CreatorId` | New migration | LOW — no data deletion | Revert migration |
| 3.3 | Replace null-forgiving operators with proper null guards | 11 controllers | LOW | Revert null checks |
| 3.4 | Replace `[DisableRequestSizeLimit]` with `[RequestSizeLimit(150*1024*1024)]` | `UploadController.cs` | NONE | Revert attribute |
| 3.5 | Add MIME type validation when Content-Type is empty | `UploadService.cs:86` | LOW | Revert check |
| 3.6 | Wrap `PurchaseService.CreateAsync` in transaction | `PurchaseService.cs` | LOW | Revert transaction |
| 3.7 | Replace `Console.WriteLine` with `ILogger` in S3ObjectStorage | `S3ObjectStorage.cs` | NONE | Revert to Console |
| 3.8 | Add webhook handlers for `charge.refunded` and `charge.dispute.created` | `StripeWebhookService.cs` | LOW | Remove handlers |
| 3.9 | Create missing governance documents (GOVERNANCE.md, ARCHITECTURE.md, SECURITY.md, DATABASE.md, TESTING.md, DOMAIN_MODELS.md) | New files | NONE | Delete files |
| 3.10 | Add automated contract-auth compliance test | New test file | NONE | Delete test |

---

*Audit complete. No code was modified. All findings traced to specific source files and line numbers.*
