# Pre-Deployment Safety Audit Report

**Date:** 2026-03-19  
**Auditor:** Automated Production Reliability Audit  
**Scope:** Full Cambrian Backend API codebase  
**Status:** AUDIT ONLY — no code changes made

---

## Executive Summary

**Total Issues Found: 88**

| Severity | Count | Description |
|----------|-------|-------------|
| CRITICAL | 12 | Issues that WILL cause data loss, financial errors, or security breaches in production |
| HIGH | 16 | Issues likely to cause user-visible failures or data inconsistency |
| MEDIUM | 35 | Issues that degrade reliability, performance, or maintainability |
| LOW | 25 | Documentation drift, dead code, minor inconsistencies |

**Top 5 Production Blockers:**
1. 10 controller endpoints silently discard request bodies — operations appear to succeed but do nothing
2. Concurrent checkout confirmation + webhook creates duplicate purchases with no DB constraint to prevent it
3. Wallet/payout operations have race conditions enabling double-withdrawal and negative balances
4. `StripeFacade.GetCheckoutSessionAsync` bare `catch { return null; }` swallows all Stripe errors silently
5. JWT key fallback applies to Staging environment, allowing tokens signed with a publicly known dev key

---

## 1. API CONTRACT VALIDATION

### CRITICAL — Silent Request Body Discards

These endpoints accept no request body in code, but the OpenAPI spec documents required inputs. The server returns a success response while performing no action. **This is a data-loss pattern.**

| # | Endpoint | File | Line | Risk |
|---|----------|------|------|------|
| 1.1 | `POST /generate` | `Controllers/AiController.cs` | 18 | Prompt/model in body silently ignored; always returns static response |
| 1.2 | `POST /admin/settings` | `Controllers/AdminController.cs` | 64 | Admin settings changes silently lost; returns "Settings updated" |
| 1.3 | `POST /admin/users/{id}/reset-password` | `Controllers/AdminController.cs` | 123 | New password silently ignored; returns "Password reset" — **security-critical** |
| 1.4 | `POST /admin/collections/curate` | `Controllers/AdminController.cs` | 213 | Collection never created |
| 1.5 | `POST /admin/tags/manage` | `Controllers/AdminController.cs` | 219 | Tags never applied |
| 1.6 | `POST /data/songs` | `Controllers/DataController.cs` | 48 | Song data never ingested |
| 1.7 | `POST /data/system` | `Controllers/DataController.cs` | 61 | System settings never stored |
| 1.8 | `POST /data/secrets` | `Controllers/DataController.cs` | 76 | Secrets never stored |
| 1.9 | `POST/PUT /payouts/settings` | `Controllers/PayoutController.cs` | 110-119 | Payout settings body accepted but never read — threshold/schedule not persisted |

**Fix:** Add `[FromBody]` parameters matching the OpenAPI spec and wire to service layer.

### CRITICAL — DTO Field Mismatches (Silent Data Loss)

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 1.10 | `CreditCreatorRequest.AmountCents` vs spec `amount` — JSON name mismatch (`amountCents` ≠ `amount`) | `DTOs/Purchases/CreditCreatorRequest.cs:11` | Client's `amount` silently defaults to 0; creator gets zero credit | Rename to `Amount` or add `[JsonPropertyName("amount")]` |
| 1.11 | `PurchaseCreateRequest` missing `amount` field defined in spec | `DTOs/Purchases/PurchaseCreateRequest.cs` | Purchase amount from client silently discarded | Add `public double? Amount { get; set; }` |

### HIGH — Response Shape Mismatches

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 1.12 | `POST /billing/checkout` returns `{url}` but spec says `{checkoutUrl}` | `Controllers/BillingController.cs:36` | Frontend `data.checkoutUrl` is `undefined`; Stripe redirect broken | Return `OkResponse(result)` which has `CheckoutUrl` property |
| 1.13 | `GET /payouts/account` returns `{accountId, status}` but spec says `{currency, pending, balance}` | `Controllers/PayoutController.cs:51` | Frontend gets wrong payout data | Return actual `PayoutAccountResponse` |
| 1.14 | `GET /stream` returns track metadata but spec says `StreamEntry` with `{id, trackId, userId, streamedAt}` | `Controllers/StreamController.cs:31-45` | Complete semantic mismatch | Return actual stream entries |
| 1.15 | `GET /settings/profile` returns `{displayName, email, tier, role}` but spec says `{displayName, bio, avatarUrl}` | `Controllers/AuthController.cs:148-154` | Profile page shows no bio/avatar; extra fields leak data | Return `SettingsProfileResponse` |
| 1.16 | `GET /community` returns `[]` (bare array) but spec says `{posts: [...]}` | `Controllers/CommunityController.cs:14` | `data.posts` is `undefined` | Return `new { posts = Array.Empty<object>() }` |

### HIGH — Query Parameter Mismatches

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 1.17 | `GET /community` accepts `page, pageSize` but spec says `take` | `Controllers/CommunityController.cs:10` | Client `?take=10` ignored | Change to `[FromQuery] int take = 20` |
| 1.18 | `GET /wallet/history` accepts `page, pageSize` (default 20) but spec says `take` (default 50) | `Controllers/WalletController.cs:48` | Client `?take=50` ignored; wrong default | Change to `[FromQuery] int take = 50` |

### HIGH — Authorization Mismatches

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 1.19 | `CatalogController` missing `[Authorize]` — manifest says `requiresAuth: true` | `Controllers/CatalogController.cs:9` | Unauthenticated catalog access | Add `[Authorize]` or update manifest |
| 1.20 | `CommunityController` missing `[Authorize]` — manifest says `requiresAuth: true` | `Controllers/CommunityController.cs:7` | Unauthenticated community access | Add `[Authorize]` |
| 1.21 | `GET /subscriptions/plans` has `[AllowAnonymous]` but manifest says `requiresAuth: true` | `Controllers/SubscriptionsController.cs:20` | Contract inconsistency | Align manifest |
| 1.22 | `GET /stream/{trackId}/audio` has `[AllowAnonymous]` but manifest says `requiresAuth: true` | `Controllers/StreamController.cs:75` | Audio streamable without auth | Align or enforce auth |
| 1.23 | `POST /admin/users/{id}/role` — no enum validation; any string role accepted | `Controllers/AdminController.cs:91` | Arbitrary roles like `"SuperAdmin"` assignable | Validate against `["User", "Admin"]` |
| 1.24 | `POST /admin/tracks/{id}/visibility` — no enum validation | `Controllers/AdminController.cs:199` | Invalid visibility states stored | Validate against `["public", "limited", "hidden"]` |

### MEDIUM — Undocumented or Inconsistent Routes

| # | Issue | File | Risk |
|---|-------|------|------|
| 1.25 | `GET /download/{trackId}/file` not in OpenAPI spec | `Controllers/DownloadController.cs:81` | Undocumented security surface |
| 1.26 | `TrackResponse` has undocumented copyright buyout fields | `DTOs/Catalog/TrackResponse.cs:22-43` | Schema drift |
| 1.27 | `UploadTrackRequest` has undocumented `CopyrightBuyoutPrice` | `DTOs/Catalog/UploadTrackRequest.cs:34` | Schema drift |
| 1.28 | `/creator/tracks` and `/creator/revenue` tagged as "Ai" in OpenAPI spec | `contracts/openapi.v1.json` | Wrong documentation grouping |
| 1.29 | `CreatorProfileDto.SocialLinks` is `List<SocialLinkDto>` but spec expects dictionary | `DTOs/CreatorProfile/CreatorProfileDto.cs:15` | Deserialization mismatch |
| 1.30 | `PaymentCheckoutRequest` lacks enum validation on `LicenseType` and `UsageType` | `DTOs/Payments/PaymentCheckoutRequest.cs` | Invalid values accepted |
| 1.31 | Duplicate entries in endpoint manifest | `contracts/endpoint-manifest.v1.json` | Manifest tooling issues |
| 1.32 | Debug endpoints missing from manifest | `contracts/endpoint-manifest.v1.json` | Manifest incompleteness |
| 1.33 | `POST /admin/purge-test-data` missing from manifest | `contracts/endpoint-manifest.v1.json` | Manifest incompleteness |

---

## 2. ENVIRONMENT VARIABLES

### CRITICAL

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 2.1 | **Hardcoded Stripe webhook secret** in `appsettings.Development.json` committed to source control | `appsettings.Development.json:12` | Secret exposure; if this is a real key it must be rotated | Remove value, use .env or user-secrets |
| 2.2 | **JWT key fallback includes Staging** — if `Jwt:Key` not configured, staging silently uses hardcoded `"***REDACTED_DEV_JWT_KEY***"` | `Program.cs:79-85` | All staging tokens signed with known key; any developer can forge tokens | Remove `Staging` from `isNonProd` for JWT fallback |
| 2.3 | **Missing `Stripe:WebhookSecret` not validated at startup** — only fails when first webhook arrives | `StripeWebhookService.cs:31` | Webhook processing broken in prod if secret not configured, discovered only at runtime | Add startup validation when `Stripe:SecretKey` is set |

### HIGH

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 2.4 | `Email:ResendApiKey` missing from `.env.example` | `.env.example` | Developers switching to `resend` get silent 401 failures | Add to `.env.example` |
| 2.5 | Resend service sends requests with empty Bearer token if API key not configured | `Infrastructure/Email/ResendEmailService.cs:28-29` | Runtime crash on first email send, not at startup | Add constructor guard |
| 2.6 | SMTP service crashes at runtime with empty host/credentials | `Infrastructure/Email/SmtpEmailService.cs:48-49` | Runtime crash on first email, not at startup | Add startup validation |
| 2.7 | Production inherits `Storage:Provider=local` and `Email:Provider=console` if env vars missing | `appsettings.Production.json` | Files lost on restart; no emails sent | Add startup validation rejecting these in prod |
| 2.8 | `Admin__Email` and `Admin__Password` missing from `.env.example` | `.env.example`, `Program.cs:407-408` | New deployments miss admin account setup | Add to `.env.example` |

### MEDIUM

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 2.9 | Connection string details logged at startup (host, port, DB name) | `Program.cs:43,52` | Information leak via logs | Reduce log verbosity |
| 2.10 | Admin email logged in plaintext at startup | `Program.cs:410` | Information leak | Log presence only, not value |
| 2.11 | `.env.example` shows `ASPNETCORE_ENVIRONMENT=Staging` as default | `.env.example:6` | Developer copies file and runs in Staging mode | Change default to `Development` |
| 2.12 | `App:CloudflarePagesSlug` missing from `.env.example` and staging `render.yaml` | `.env.example`, `render.yaml` | CORS may block Cloudflare preview deployments | Add to both |
| 2.13 | `App:VercelProjectSlug` missing from `.env.example` | `.env.example` | Undocumented config requirement | Add to `.env.example` |
| 2.14 | `JwtTokenService` / `JwtOptions` never registered in DI (dead code) | `Infrastructure/Security/JwtTokenService.cs` | Maintenance trap | Remove or wire up |
| 2.15 | `EmailOptions.SendGridApiKey` defined but never used | `Infrastructure/Options/EmailOptions.cs:21` | Dead code confusion | Remove |
| 2.16 | Swagger enabled in Staging | `Program.cs:339-343` | Full API surface exposed if staging is public | Restrict access or disable |

---

## 3. ERROR HANDLING

### CRITICAL

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 3.1 | **`StripeFacade.GetCheckoutSessionAsync` bare `catch { return null; }`** | `Infrastructure/Stripe/StripeFacade.cs:126-131` | ALL Stripe errors silently swallowed — paid sessions report as failed with zero logging | Add `catch (StripeException ex)` with logging, rethrow on non-404 errors |
| 3.2 | **`PayoutService.RequestAsync` balance check + debit not atomic** — no transaction wrapping | `Application/Services/PayoutService.cs:72-167` | Concurrent requests: double-withdrawal, negative wallet balance | Use `AtomicWithdrawAsync` or wrap in serializable transaction |
| 3.3 | **`AuthService.ForgotPasswordAsync` catches email failure but never logs it** — `ILogger` not injected | `Application/Services/AuthService.cs` | Password reset emails fail silently; user never receives reset code | Inject `ILogger` and log the exception |
| 3.4 | **`WebhookController` only catches `StripeException` and specific `InvalidOperationException`** | `Controllers/WebhookController.cs:29-44` | Any other exception (DB, null ref, etc.) returns 500 to Stripe → infinite retries → potential duplicate processing | Add general `catch (Exception ex)` that logs and returns 200 to stop retries |
| 3.5 | **Migration errors swallowed at startup** — app continues with un-migrated database | `Program.cs:398-401` | Database schema mismatch causes cryptic runtime errors | Fail startup on migration error in Production |

### HIGH

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 3.6 | `ExceptionMiddleware` doesn't map `FormatException` | `Middleware/ExceptionMiddleware.cs:35-42` | Malformed GUIDs return 500 instead of 400 | Add `FormatException => 400` mapping |
| 3.7 | `ExceptionMiddleware` doesn't map `DbUpdateException` | `Middleware/ExceptionMiddleware.cs:35-42` | Unique constraint violations return 500 instead of 409 | Add `DbUpdateException => 409` mapping |
| 3.8 | `ExceptionMiddleware` doesn't map `OperationCanceledException` | `Middleware/ExceptionMiddleware.cs:35-42` | Client disconnects logged as errors | Add `OperationCanceledException` with no 500 response |
| 3.9 | Webhook idempotency race — `DbUpdateException` on unique constraint not caught | `Infrastructure/Stripe/StripeWebhookService.cs:151-153` | Second concurrent webhook returns 500, Stripe retries indefinitely | Catch `DbUpdateException` and treat as "already processed" |

### MEDIUM

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 3.10 | `Guid.Parse` on untrusted input in `PaymentService.GetResultAsync` | `Application/Services/PaymentService.cs:65` | `FormatException` → unhandled 500 | Use `Guid.TryParse` |
| 3.11 | `Guid.Parse` on untrusted input in `PaymentService.ProcessAsync` | `Application/Services/PaymentService.cs:78` | `FormatException` → unhandled 500 | Use `Guid.TryParse` |
| 3.12 | `Guid.Parse` on untrusted input in `LibraryService.SaveAsync` | `Application/Services/LibraryService.cs:54` | `FormatException` → unhandled 500 | Use `Guid.TryParse` |
| 3.13 | `LicensesController.DownloadPdf` uses `licenseId[..8]` without length check | `Controllers/LicensesController.cs:78` | `ArgumentOutOfRangeException` if < 8 chars | Use `licenseId[..Math.Min(8, licenseId.Length)]` |
| 3.14 | `DataController` imports domain entity directly (`UserManager<ApplicationUser>`) | `Controllers/DataController.cs:13` | Architecture violation; tight coupling | Inject service interface |
| 3.15 | Admin `PurgeTestData` runs raw SQL DELETEs without a transaction | `Persistence/Repositories/AdminRepository.cs:81-131` | Partial purge on failure → inconsistent state | Wrap in `BeginTransactionAsync`/`CommitAsync` |

---

## 4. EDGE CASES

### CRITICAL — Race Conditions

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 4.1 | **Concurrent checkout confirmation creates duplicate purchases** — read-then-write without locking | `Application/Services/CheckoutService.cs:151-153` | Duplicate purchase records, double library items, double creator credits | Add unique constraint on `Purchase(BuyerId, TrackId, LicenseType)` |
| 4.2 | **Webhook + ConfirmAsync double-fulfillment race** — both check for existing purchases, find none, both create | `StripeWebhookService.cs:317-318`, `CheckoutService.cs:150-213` | Duplicated financial records | Make `ConfirmAsync` read-only or use `StripeSessionId` unique index |
| 4.3 | **Copyright buyout in `ConfirmAsync` is not atomic** — checks `track.ExclusiveSold` in memory then sets it | `Application/Services/CheckoutService.cs:224-233` | Two concurrent buyouts both succeed — copyright transferred to two buyers | Use atomic SQL UPDATE like the webhook path |
| 4.4 | **Payout request has no duplicate prevention** — two quick clicks create two payouts and double-debit | `Application/Services/PayoutService.cs:72-167` | Double Stripe transfer; wallet overdraft | Wrap in serializable transaction; add "pending payout" check |

### HIGH — Race Conditions

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 4.5 | **`WalletRepository.AddTransactionAsync` has no concurrency control** | `Persistence/Repositories/WalletRepository.cs:32-36` | Creator credited twice if webhook and confirm run concurrently | Add `RelatedPurchaseId` unique constraint for credit type |
| 4.6 | **Wallet balance can go negative** via separate check-then-debit in payout flow | `PayoutService.cs:96-101`, `WalletRepository.cs:38-74` | Financial inconsistency | Use `AtomicWithdrawAsync` for payouts |

### MEDIUM — Null Safety

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 4.7 | `User.FindFirstValue(ClaimTypes.NameIdentifier)!` null-forgiving operator used across all controllers | Multiple controllers | NullReferenceException → 500 if JWT lacks `sub` claim | Add `GetRequiredUserId()` helper in `BaseController` |
| 4.8 | `track.Creator` always null in webhook path — `FindAsync` doesn't Include | `StripeWebhookService.cs:342-344` | Library items always have empty Artist field | Use `.Include(t => t.Creator).FirstOrDefaultAsync()` |

### MEDIUM — Performance / Loading

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 4.9 | **Webhook body read without size limit** — `ReadToEndAsync()` on unauthenticated endpoint | `Controllers/WebhookController.cs:24-25` | DoS via multi-GB payload → OOM | Add `[RequestSizeLimit(1_048_576)]` |
| 4.10 | **`UploadController` uses `[DisableRequestSizeLimit]`** — Kestrel buffers entire request | `Controllers/UploadController.cs:26` | Memory exhaustion before service-level 100MB check runs | Replace with `[RequestSizeLimit(110_000_000)]` |
| 4.11 | `AdminRepository.GetDashboardStatsAsync` loads ALL completed purchases into memory | `Persistence/Repositories/AdminRepository.cs:25` | OOM/timeout as data grows | Use `CountAsync`/`SumAsync` |
| 4.12 | `MarketplaceIntegrityService.RunAuditAsync` loads entire tables into memory | `Persistence/Services/MarketplaceIntegrityService.cs:58-86` | OOM as data grows | Use SQL JOINs |
| 4.13 | `CreatorService.GetTracksAsync` loads ALL creator tracks then paginates in memory | `Application/Services/CreatorService.cs:19-51` | Performance degradation at scale | Push Skip/Take into repository |
| 4.14 | N+1 query: PayoutService/CreatorService fetch purchases per-track in a loop | `Application/Services/PayoutService.cs:42-48`, `CreatorService.cs:58-63` | DB round-trip explosion | Add batch `GetByTrackIdsAsync` |
| 4.15 | No `CancellationToken` propagation throughout async chain | All controllers, services, repositories | Wasted resources on client disconnect | Add `CancellationToken` parameters |

### LOW — Pagination

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 4.16 | `WalletController.History` ignores `page` parameter — always returns page 1 | `Controllers/WalletController.cs:48-56` | Pagination broken | Pass both `page` and `pageSize` |
| 4.17 | Pagination endpoints return no `totalCount` — client can't know when to stop | `Controllers/CatalogController.cs` | Poor UX for paginated views | Return `{ items, totalCount, page, pageSize }` envelope |

---

## 5. DATA INTEGRITY

### CRITICAL — Missing Database Constraints

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 5.1 | **No unique constraint on `Purchase(BuyerId, TrackId, LicenseType)`** | `Persistence/CambrianDbContext.cs:75-95` | Duplicate purchases possible under concurrent requests | Add unique filtered index on completed status |
| 5.2 | **No unique constraint on `WalletTransaction.RelatedPurchaseId` for credits** | `Persistence/CambrianDbContext.cs:178-185` | Creator paid multiple times for same sale | Add unique filtered index where `Type = 'credit'` |

### HIGH — Referential Integrity

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 5.3 | **Cascade delete on Invoice → User** — deleting user deletes invoices | `Persistence/CambrianDbContext.cs:159-161` | Financial records lost; regulatory violation | Change to `DeleteBehavior.Restrict` |
| 5.4 | **Cascade delete on WalletTransaction → User** — financial audit trail lost | `Persistence/CambrianDbContext.cs:184` | Cannot audit historical wallet activity after user deletion | Change to `DeleteBehavior.Restrict` |
| 5.5 | `WalletTransaction.RelatedPurchaseId` has no FK to `Purchase` | `Domain/Entities/WalletTransaction.cs:17` | Orphaned references possible | Add FK with `SetNull` cascade |
| 5.6 | `LicenseCertificate.TrackId` is a string (CambrianTrackId), not a FK | `Domain/Entities/LicenseCertificate.cs:11` | Orphaned certificates if track deleted | Add proper FK |
| 5.7 | `CreatorProfile` has no FK to `ApplicationUser` | `Persistence/CambrianDbContext.cs:238-250` | Orphaned profiles after user deletion | Add FK |
| 5.8 | `TrackCollection` has no FK to `ApplicationUser` | `Persistence/CambrianDbContext.cs:252-261` | Orphaned collections | Add FK |
| 5.9 | `TrackCollection.TrackIds` is comma-separated string with no FK relationship | `Domain/Entities/TrackCollection.cs:16` | Broken collections when tracks deleted | Use join table or validate on read |

### MEDIUM — Consistency

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 5.10 | `ApplicationUser.WalletBalanceCents` denormalized field never updated — always 0 | `Domain/Entities/ApplicationUser.cs:22` | Stale data if any code reads this field | Remove or update atomically |
| 5.11 | `Subscription` allows multiple active subscriptions per user | `Persistence/CambrianDbContext.cs:138-145` | Conflicting active plans | Add unique filtered index where `Status = 'active'` |
| 5.12 | `Invoice.PurchaseId` has no unique constraint | `Persistence/CambrianDbContext.cs:156-167` | Duplicate invoices possible | Add unique index |
| 5.13 | `PurchaseService.CreateAsync` duplicate check includes refunded purchases | `Application/Services/PurchaseService.cs:61-63` | Blocks legitimate re-purchases after refund | Filter: `Status == "completed"` |
| 5.14 | `Track.Price` is `double` — floating point imprecision for financial data | `Domain/Entities/Track.cs:25` | `(int)(29.99 * 100)` could produce 2998 instead of 2999 | Use `Math.Round` consistently |
| 5.15 | Platform fee inconsistency: code uses 15%, admin dashboard shows 20% | `CheckoutService.cs:261`, `AdminController.cs:56` | Misleading admin UI | Use single config-driven rate |
| 5.16 | `Payout.AmountCents` is `int` but `WalletTransaction.AmountCents` is `long` | `Domain/Entities/Payout.cs:12` | Silent integer overflow for amounts > ~$21M | Change to `long` |

### LOW — Missing Indexes

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 5.17 | No explicit index on `Purchase.BuyerId` | `CambrianDbContext.cs` | Slow queries | Add `HasIndex(p => p.BuyerId)` |
| 5.18 | No explicit index on `Purchase.TrackId` | `CambrianDbContext.cs` | Slow queries | Add `HasIndex(p => p.TrackId)` |
| 5.19 | No explicit index on `WalletTransaction.UserId` | `CambrianDbContext.cs` | Full table scans for balance/history | Add `HasIndex(w => w.UserId)` |
| 5.20 | No explicit index on `Payout.CreatorId` | `CambrianDbContext.cs` | Slow queries | Add `HasIndex(p => p.CreatorId)` |

### LOW — Dead Code

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 5.21 | `CreatorBalance` entity — `CreatorId` is GUID but Identity uses string IDs | `Domain/Entities/CreatorBalance.cs:6` | Confusion; not in DbContext | Remove if unused |
| 5.22 | `User` entity separate from `ApplicationUser` — not in DbContext | `Domain/Entities/User.cs` | Confusion | Remove if unused |
| 5.23 | `License`, `Payment`, `TrackFile` entities not in DbContext | Various entity files | Confusion | Remove or implement |
| 5.24 | CSRF token endpoint returns random GUID with no server-side validation | `Controllers/AuthController.cs:107-109` | Misleading — no actual CSRF protection | Remove or implement properly |

---

## 6. DEPLOYMENT RISKS

### CRITICAL — Production vs Local Differences

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 6.1 | **Production config has no `Storage` or `Email` sections** — inherits `local`/`console` defaults | `appsettings.Production.json` | If env vars missing: files lost on restart, no emails sent | Add startup validation rejecting `local`/`console` in Production |
| 6.2 | **Auto-migration continues on failure** — app starts with un-migrated DB | `Program.cs:398-401` | Schema mismatch causes cryptic errors | Fail startup in Production on migration error |
| 6.3 | **Debug endpoints (`/debug/*`) accessible with Admin JWT** — no IP restriction | `Controllers/DebugController.cs` | Diagnostic endpoints exposing user state, purchases, webhook logs | Add IP allowlist or environment gate |

### HIGH — Configuration Drift

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 6.4 | Production CORS missing `www.cambrianmusic.com` in config — only added via hardcoded array | `appsettings.Production.json:16`, `Program.cs:194` | Duplication between config and code; maintenance risk | Add to config, remove hardcode |
| 6.5 | Production `render.yaml` section is commented out | `render.yaml:108-172` | Production deployment not automated; manual setup required | Uncomment when ready; ensure all env vars set |
| 6.6 | Render production uses `starter` plan for DB | `render.yaml:20` | Free DB plan has connection limits and no persistent storage guarantee | Use paid plan for production |

### MEDIUM — Operational Risks

| # | Issue | File | Risk | Fix |
|---|-------|------|------|-----|
| 6.7 | `Kestrel.MaxRequestBodySize` set to 150MB globally | `Program.cs:136` | All endpoints accept 150MB requests, not just uploads | Apply per-endpoint |
| 6.8 | `IPaymentGateway` registered as `Singleton` — holds config reference | `Program.cs:287` | If `App:FrontendUrl` changes at runtime, singleton keeps stale value | Register as `Scoped` |
| 6.9 | Storage and Email services registered as `Singleton` | `Program.cs:310,326,330` | Options changes not reflected; potential issues with scoped DbContext | Register as `Scoped` |
| 6.10 | `HandleSubscriptionDeleted` only logs — does not actually downgrade user tier | `StripeWebhookService.cs:515-529` | Cancelled subscribers keep premium access indefinitely | Implement user lookup and tier downgrade |
| 6.11 | `HandleInvoicePaymentFailed` only logs — no user notification | `StripeWebhookService.cs:536-544` | Users unaware of payment failures | Implement notification |
| 6.12 | Cascade delete on Track → AbuseReports — moderation records lost | `CambrianDbContext.cs:130` | Investigation audit trail deleted with track | Change to `Restrict` or `SetNull` |

---

## Priority Remediation Order

### Phase 1: Must Fix Before Production (CRITICAL)

1. **Add unique constraint on `Purchase(BuyerId, TrackId, LicenseType)`** — prevents duplicate financial records (5.1)
2. **Fix `StripeFacade.GetCheckoutSessionAsync` bare catch** — surface Stripe errors (3.1)
3. **Add general catch in `WebhookController`** — return 200 to prevent Stripe retry storms (3.4)
4. **Remove Staging from JWT key fallback** — prevent known-key token forgery (2.2)
5. **Wire up all 10 silently-discarded request bodies** — especially admin reset password (1.1-1.9)
6. **Make payout operations atomic** — prevent double-withdrawal (3.2, 4.4, 4.6)
7. **Add unique constraint on `WalletTransaction.RelatedPurchaseId` for credits** — prevent double-payment (5.2)
8. **Fix copyright buyout atomicity in `ConfirmAsync`** — prevent double-transfer (4.3)
9. **Add startup validation for Storage/Email in Production** — prevent silent fallback to local/console (6.1)
10. **Fail startup on migration error in Production** (6.2)

### Phase 2: Should Fix Before Production (HIGH)

11. Fix all response shape mismatches (1.12-1.16)
12. Fix query parameter mismatches (1.17-1.18)
13. Remove hardcoded Stripe webhook secret from appsettings.Development.json (2.1)
14. Add startup validation for email provider credentials (2.5-2.6)
15. Fix cascade delete on Invoice and WalletTransaction (5.3-5.4)
16. Map `FormatException` and `DbUpdateException` in ExceptionMiddleware (3.6-3.8)
17. Handle webhook idempotency race (3.9)
18. Add request size limits to webhook endpoint (4.9)

### Phase 3: Should Fix Soon (MEDIUM)

19. Fix all `Guid.Parse` → `Guid.TryParse` (3.10-3.12)
20. Add missing FKs (5.5-5.9)
21. Fix N+1 queries and in-memory pagination (4.13-4.14)
22. Add `CancellationToken` propagation (4.15)
23. Fix platform fee inconsistency (5.15)
24. Add missing environment variable documentation (2.4, 2.8, 2.12-2.13)
25. Implement subscription deletion handler (6.10)

---

*This audit identifies risks only. No code changes have been made. Each fix should be implemented with minimal scope and validated independently.*
