# Security & Flow Audit — 2026-03-29

> Full end-to-end audit of auth, payments, creator, upload, admin, and contract flows.
> Covers happy paths, broken paths, attacker behavior, contract drift, and state inconsistencies.

---

## 🔴 CRITICAL (must fix before launch)

### C1. Exclusive License Double-Sale — User Pays, Gets Nothing

- **Flow:** Marketplace → Checkout → Webhook
- **Bug:** Two users can both create checkout sessions for the same exclusive license. The `ExclusiveSold` check in `CreateCheckoutAsync()` is a soft read — not a lock. Both sessions go to Stripe. Both payments succeed. First webhook wins the atomic CAS UPDATE; second webhook silently returns without creating a Purchase, Library entry, License, or Invoice. The buyer paid but received nothing.
- **File:** `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs` lines 298–314, `src/Cambrian.Application/Services/CheckoutService.cs` line 60
- **Cause:** No reservation/lock between CreateCheckout and webhook fulfillment. The CAS in the webhook is too late — Stripe already captured the payment.
- **State left behind:** `StripeWebhookEvents` marked `"completed"`, zero Purchase/Library/License records for losing buyer, Stripe holds the money.
- **Fix:** Either (a) pre-reserve exclusivity at CreateCheckout time with a TTL lock table, (b) issue an automatic Stripe refund when the CAS fails, or (c) create a `"refund_pending"` Purchase record with the StripeSessionId for manual processing. Option (b) is simplest.

### C2. Email Change Without Confirmation — Account Takeover

- **Flow:** Auth → Settings → Change Email
- **Bug:** `POST /settings/email` changes the email immediately after password verification, with no confirmation link to the new address and no notification to the old address. Existing sessions are not invalidated.
- **File:** `src/Cambrian.Application/Services/AuthService.cs` lines 309–335
- **Cause:** The code generates a change token and applies it in the same request — no out-of-band verification.
- **Attack:** Attacker with a compromised password calls the endpoint, changes email to their own, locks out the original owner permanently.
- **Fix:** Send confirmation link to NEW email. Only apply the change after click-through. Notify OLD email of the request. Invalidate all sessions on change.

### C3. Download Without Purchase — Library Save Bypasses Entitlement

- **Flow:** Marketplace → Library → Download
- **Bug:** `POST /library` lets any authenticated user save any public track (creates a `LibraryItem` with `PurchaseId = null`). `GET /download/{trackId}` checks only that a `LibraryItem` exists — not that `PurchaseId` is set or that the associated Purchase has `Status = "completed"`.
- **File:** `src/Cambrian.Api/Controllers/DownloadController.cs` lines 34–73, `src/Cambrian.Application/Services/LibraryService.cs` lines 57–81
- **Cause:** Download entitlement check is `libraryItem != null` instead of `libraryItem?.PurchaseId != null && purchase.Status == "completed"`.
- **Fix:**
  ```csharp
  if (libraryItem?.PurchaseId is null)
      return ForbiddenResponse("You must purchase this track before downloading.");
  var purchase = await _purchases.GetByIdAsync(libraryItem.PurchaseId.Value);
  if (purchase?.Status != "completed")
      return ForbiddenResponse("Purchase not completed.");
  ```

### C4. Hidden/Copyright-Transferred Tracks Still Accessible by ID

- **Flow:** Marketplace → Track Detail / Streaming
- **Bug:** `GET /tracks/{trackId}` and `GET /stream/{trackId}/audio` (anonymous) do not check `Track.Visibility` or `Track.Status`. Admin hides or copyright-transfers a track → anyone with the ID still streams full audio.
- **File:** `src/Cambrian.Api/Controllers/CatalogController.cs` lines 91–104, `src/Cambrian.Api/Controllers/StreamController.cs` lines 77–94
- **Cause:** Catalog listing filters hidden tracks correctly; direct-access endpoints skip the filter.
- **Fix:** Add to both endpoints:
  ```csharp
  if (track.Visibility != "public" || track.Status == "copyright_transferred")
      return NotFoundResponse("Track not found.");
  ```

---

## 🟠 HIGH

### H1. No Email Verification on Registration

- **Flow:** Auth → Register
- **Bug:** Local registration never enforces email confirmation. Users get immediate full API access. An attacker can register with a victim's email address and squat the account.
- **File:** `src/Cambrian.Application/Services/AuthService.cs` lines 103–140
- **Fix:** Either enforce email confirmation before API access, or document as accepted risk and prevent sensitive operations (email change, payout) until confirmed.

### H2. Webhook Self-Purchase Not Re-Verified

- **Flow:** Payments → Webhook
- **Bug:** `CreateCheckoutAsync()` blocks creator self-purchase at checkout creation (line 70). But the webhook (`HandleTrackPurchase`) trusts `clientReferenceId` without re-verifying `buyer != creator`. If track ownership changes between checkout creation and webhook processing, the check is stale.
- **File:** `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs` lines 281–283
- **Fix:** Re-verify in `HandleTrackPurchase`:
  ```csharp
  if (string.Equals(track.CreatorId, userId, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("Creator cannot purchase own track.");
  ```

### H3. Collection Track Ownership Not Validated

- **Flow:** Creator → Collections
- **Bug:** `POST /creator-profile/me/collections` accepts arbitrary track IDs without verifying the creator owns them. A creator can add another creator's tracks to their collection, causing attribution confusion.
- **File:** `src/Cambrian.Persistence/Repositories/CreatorProfileRepository.cs` lines 149–177
- **Fix:** Validate all track IDs belong to the authenticated creator before saving.

### H4. Webhook Price Not Re-Validated at Fulfillment

- **Flow:** Payments → Webhook → Purchase
- **Bug:** `ConfirmAsync()` accepts `session.AmountTotal` from Stripe without comparing it to the current track price. If the track price changed between checkout creation and webhook, the Purchase records the stale amount and the creator is credited incorrectly.
- **File:** `src/Cambrian.Application/Services/CheckoutService.cs` line 289
- **Fix:** Re-read track price in `ConfirmAsync`, compare to `session.AmountTotal`, log a warning if mismatched (don't block — price may have legitimately changed, but flag for audit).

### H5. Missing Rate Limiting on Sensitive Endpoints

- **Flow:** Auth, Payments
- **Endpoints missing `[EnableRateLimiting("auth")]`:**
  - `POST /auth/set-password` — allows unlimited password set attempts for OAuth users
  - `POST /auth/link-google` — allows unlimited Google linking attempts
  - `POST /auth/verify-code` — 10/min global is too generous for brute-forcing 8-char codes per email
- **Fix:** Add `[EnableRateLimiting("auth")]` to all three. Implement per-email attempt counter for verify-code with lockout after 5 failures.

### H6. Anonymous Audio Streaming With No Controls

- **Flow:** Marketplace → Stream
- **Bug:** `GET /stream/{trackId}/audio` is `[AllowAnonymous]` with no authentication, no rate limiting, no bandwidth throttling, and no stream duration tracking. Full-quality audio is freely redistributable via presigned URL (1-hour expiry).
- **File:** `src/Cambrian.Api/Controllers/StreamController.cs` line 77
- **Fix:** Reduce presigned URL expiry to 15 minutes. Add per-IP rate limiting. Consider requiring auth for full-quality streams and serving lower-quality previews for anonymous users.

---

## 🟡 MEDIUM

### M1. Logout Is a No-Op — Tokens Valid for 2 Hours Post-Logout

- **Flow:** Auth → Logout
- **Bug:** `POST /auth/logout` returns a success message but does not invalidate the JWT. Token remains valid until natural 120-minute expiry.
- **File:** `src/Cambrian.Api/Controllers/AuthController.cs` lines 234–238
- **Fix:** Implement a token blacklist (in-memory cache is fine given the 120-minute window) or reduce token expiry to 15 minutes with a refresh token flow.

### M2. RecoverUsername Sends Wrong Field

- **Flow:** Auth → Recover Username
- **Bug:** `POST /auth/recover-username` sends `DisplayName` in the recovery email instead of `UserName`.
- **File:** `src/Cambrian.Application/Services/AuthService.cs` lines 267–295
- **Fix:** Send `user.UserName` instead of `user.DisplayName`.

### M3. Payout Balance Not Accounting for Pending Payouts

- **Flow:** Payments → Payouts
- **Bug:** `AtomicWithdrawAsync()` uses serializable isolation correctly, but if a Stripe transfer fails and the wallet is refunded, a second payout request can succeed before the first one is reconciled. Multiple pending payouts can exceed actual balance.
- **File:** `src/Cambrian.Application/Services/PayoutService.cs` lines 100–108
- **Fix:** Sum all "pending" payouts when checking available balance, or implement a queue where only one payout can be in-flight at a time.

### M4. License Issuance Failure Silently Swallowed

- **Flow:** Payments → Webhook → License
- **Bug:** If `LicenseService.IssueCertificateAsync()` throws, the exception is caught and logged, but the Purchase is still saved as `"completed"`. User has no license certificate for commercial use.
- **File:** `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs` lines 449–468
- **Fix:** Either retry license issuance, mark Purchase with a `"license_pending"` flag, or create an admin queue for manual resolution.

### M5. Creator Self-Follow Allowed

- **Flow:** Creator → Follow
- **Bug:** `POST /creator-profile/{slug}/follow` does not check if the authenticated user is the creator being followed. Inflates follower counts.
- **File:** `src/Cambrian.Api/Controllers/CreatorProfileController.cs` lines 224–254
- **Fix:** `if (creator.UserId == userId) return ErrorResponse("You cannot follow yourself.");`

### M6. Profile Slug Race Condition

- **Flow:** Creator → Profile Setup
- **Bug:** Slug uniqueness check (`GetBySlugAsync`) and creation are not atomic. Two concurrent requests for the same slug can both pass the check.
- **File:** `src/Cambrian.Api/Controllers/CreatorProfileController.cs` lines 83–93
- **Fix:** Rely on database unique constraint and handle `DbUpdateException` with a user-friendly conflict response.

### M7. Stream Inflation — No Completion Proof

- **Flow:** Marketplace → Stream
- **Bug:** `POST /stream/start` accepts a `trackId` with no validation that the user actually listened. Multiple concurrent stream sessions per user are allowed. A script can inflate stream counts trivially.
- **File:** `src/Cambrian.Api/Controllers/StreamController.cs`
- **Fix:** Require stream completion proof (minimum duration), add per-user rate limiting (max 1 start per 10 seconds per track).

### M8. Missing Content-Security-Policy Header

- **Flow:** All HTTP responses
- **Bug:** No CSP header is set. All other security headers (HSTS, X-Frame-Options, X-Content-Type-Options, Referrer-Policy) are present.
- **File:** `src/Cambrian.Api/Program.cs` lines 254–264
- **Fix:** Add `Content-Security-Policy: default-src 'self'` (adjust for actual asset sources).

### M9. Negative Track Prices Not Validated

- **Flow:** Creator → Upload
- **Bug:** No validation that `NonExclusivePriceCents`, `ExclusivePriceCents`, or `CopyrightBuyoutPriceCents` are >= 0. A negative price could theoretically credit the buyer.
- **File:** `src/Cambrian.Application/Services/UploadService.cs`
- **Fix:** Add `if (price < 0) throw new ArgumentException("Price cannot be negative.");`

---

## 🟢 LOW

### L1. Staging Error Responses Leak Stack Traces

- `ExceptionMiddleware` returns raw `ex.Message` in staging. Should use production-like generic messages.
- **File:** `src/Cambrian.Api/Middleware/ExceptionMiddleware.cs` lines 31–33

### L2. Clock Skew of 2 Minutes Is Generous

- JWT validation allows 2-minute skew. Consider reducing to 30–60 seconds.
- **File:** `src/Cambrian.Api/Program.cs` line 92

### L3. Username Regex Allows Confusing Patterns

- `^[a-z0-9_-]+$` allows `---`, `___`, or all-numeric usernames.
- **File:** `src/Cambrian.Api/Controllers/AuthController.cs` line 277

### L4. Presigned URL Expiry (1 Hour) Is Long

- S3 presigned URLs for audio and downloads expire after 1 hour. 15–30 minutes is more appropriate.
- **File:** `src/Cambrian.Infrastructure/Storage/S3ObjectStorage.cs` lines 64–75

### L5. No Webhook Replay Mechanism

- Failed webhooks are persisted in `StripeWebhookEvents` with `Status = "failed"`, but there's no admin endpoint to list or replay them.
- **Fix:** Add `GET /admin/webhooks/failed` and `POST /admin/webhooks/{eventId}/replay`.

### L6. Stripe Customer Duplication

- `FindOrCreateCustomerAsync()` has a TOCTOU race — two concurrent checkouts for the same email can create duplicate Stripe customers.
- **File:** `src/Cambrian.Infrastructure/Stripe/StripeFacade.cs` lines 140–157

---

## 🔁 CONTRACT DRIFT SUMMARY

### Endpoints in Code but NOT in OpenAPI/Manifest

| Endpoint | Controller | Notes |
|----------|-----------|-------|
| `GET /auth/google/status` | AuthController | Google OAuth config check |
| `POST /auth/google` | AuthController | Google OAuth login |
| `POST /auth/set-password` | AuthController | Password setup for OAuth users |
| `POST /auth/link-google` | AuthController | Link Google to existing account |
| `POST /auth/refresh` | AuthController | Token refresh |
| `POST /auth/set-username` | AuthController | Creator onboarding |
| `GET /auth/username-availability` | AuthController | Public username check |
| `GET /debug/user/{userId}` | DebugController | Admin diagnostic |
| `GET /debug/webhooks` | DebugController | Webhook history |
| `GET /debug/consistency` | DebugController | Library consistency check |
| `POST /creator-profile/{slug}/follow` | CreatorProfileController | Follow creator |
| `DELETE /creator-profile/{slug}/follow` | CreatorProfileController | Unfollow creator |
| `GET /creator-profile/{slug}/follow` | CreatorProfileController | Follow status |
| `GET /activity/new` | ActivityController | Recent uploads |
| `GET /activity/sales` | ActivityController | Recent purchases |
| `GET /activity/trending` | ActivityController | Trending tracks |
| `GET /tracks/trending` | CatalogController | Trending alias |
| `POST /api/uploads/creator-image-url` | CreatorsController | Presigned upload URL |

### HTTP Method Mismatches

| Endpoint | Manifest Says | Code Says |
|----------|--------------|-----------|
| `/admin/payouts/requests` | POST | GET |

### Auth Mismatches

| Endpoint | Manifest Says | Code Says |
|----------|--------------|-----------|
| `/stream/{trackId}/audio` | Requires auth | `[AllowAnonymous]` |

### Other Drift

- `endpoint-manifest.v1.json` has **duplicate entries** (lines 704–712 duplicate lines 686–718) for `/health/storage`, `/stream/{trackId}/audio`, `/webhook/stripe`.
- Multiple stub endpoints return 501 (`/admin/settings`, `/admin/tracks/{id}/feature`, `/admin/tracks/{id}/pin`, `/admin/collections/curate`, `/admin/tags/manage`) — should be documented as unimplemented or removed from contract.

### Recommended Source of Truth

Per `governance/SOURCE_OF_TRUTH.md`, `contracts/API_CONTRACTS.md` is the primary SoT. **The code has drifted from the contract.** Either update the contract to match reality, or remove the undocumented endpoints.

---

## 🧠 SYSTEMIC RISKS

### 1. Auth State

- **No token revocation.** Logout is cosmetic. Stolen tokens are valid for 2 hours.
- **No email verification.** Local accounts are immediately active. Email squatting is possible.
- **Role in JWT can go stale.** `/auth/me` re-issues tokens (good), but any endpoint checking JWT claims between refreshes sees stale roles.

### 2. Payment State

- **Dead-letter gap.** When webhook fulfillment fails (exclusive race, missing track, DB error), Stripe has the money but Cambrian has no Purchase record and no automated refund path. The `[DEAD-LETTER]` log warning is the only signal.
- **No price re-validation.** Track price at checkout creation time may differ from price at fulfillment time. No reconciliation.
- **License issuance is fire-and-forget.** Failure is logged but doesn't block the Purchase from completing. Users can end up with completed purchases and no license.

### 3. Access Control

- **Download entitlement is broken.** Library save → download works without purchase.
- **Visibility enforcement is incomplete.** Hidden tracks are filtered from listings but directly accessible via ID.
- **Collection ownership is not validated.** Creators can reference any track ID.

### 4. Architecture

- **Contract drift is significant.** 18+ endpoints exist in code but not in the OpenAPI spec. Frontend may be calling undocumented endpoints that could be removed or changed without notice.
- **Dual identity system.** `ApplicationUser.Role` + `Creators` table + JWT claims create multiple sources of truth for creator status. Synchronization bugs are inevitable.
- **No webhook replay.** Failed webhook events are stored but cannot be reprocessed. Manual DB intervention is the only recovery path.

---

## Priority Remediation Order

| # | Item | Severity | Effort |
|---|------|----------|--------|
| 1 | C1: Auto-refund on exclusive race loss | Critical | Medium |
| 2 | C3: Download entitlement check | Critical | Easy |
| 3 | C4: Visibility check on direct access | Critical | Easy |
| 4 | C2: Email change confirmation flow | Critical | Medium |
| 5 | H5: Rate limit verify-code, set-password, link-google | High | Easy |
| 6 | H2: Re-verify buyer ≠ creator in webhook | High | Easy |
| 7 | H1: Email verification on registration | High | Medium |
| 8 | H3: Collection track ownership validation | High | Easy |
| 9 | H4: Price re-validation in ConfirmAsync | High | Easy |
| 10 | M9: Negative price validation | Medium | Easy |
| 11 | Contract drift: Update OpenAPI spec | Medium | Medium |
| 12 | M1: Token revocation or shorter expiry | Medium | Medium |
| 13 | L5: Webhook replay admin endpoint | Low | Medium |

---

**Audit performed:** 2026-03-29
**Files reviewed:** ~40 controllers, services, repositories, and middleware files (~5,000+ lines of security-critical code)
**Methodology:** Automated agent-based analysis of auth, payment, creator, upload, admin, middleware, and contract layers
