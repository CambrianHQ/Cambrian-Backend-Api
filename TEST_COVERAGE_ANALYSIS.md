# Test Coverage Analysis

## Summary

The test suite has solid coverage of the **core happy-path flows** — auth, catalog, checkout,
purchase, licensing, and Stripe webhooks. However, several important areas are either entirely
absent or have significant gaps. The sections below are ordered by priority.

---

## 1. `ExceptionMiddleware` — no tests at all

**File:** `src/Cambrian.Api/Middleware/ExceptionMiddleware.cs`

The middleware maps every thrown exception to an HTTP status code. It is the first line of
defense for every endpoint, yet it has zero test coverage.

**What should be tested:**

| Scenario | Expected behaviour |
|---|---|
| `ForbiddenException` thrown | 403 response |
| `UnauthorizedAccessException` thrown | 401 response |
| `KeyNotFoundException` thrown | 404 response |
| `ArgumentException` thrown | 400 response |
| `InvalidOperationException` thrown | 400 response |
| Unhandled generic `Exception` | 500 response |
| Development env + generic exception | Full message in response body |
| Production env + generic exception | Generic `"An unexpected error occurred."` message (no leakage) |
| Response already started | No second write attempt (no `InvalidOperationException`) |

The production-vs-development message redaction is particularly important from a security
standpoint and should be covered with at least one test each.

---

## 2. `AdminController` — no tests at all

**File:** `src/Cambrian.Api/Controllers/AdminController.cs`

This controller contains the most sensitive operations in the entire application:

- User suspension, reactivation, role assignment, and forced password reset
- Creator verification
- Payout approval and rejection
- Track removal, hiding, flagging, and visibility changes
- `POST /admin/purge-test-data` — destroys all non-admin data

**Specific gaps:**

- `PurgeTestData` has two guards (`_env.IsProduction()` check, `?confirm=yes` check) that
  are completely untested. A bug here in the production guard could be catastrophic.
- `ApprovePayout`/`RejectPayout` return 404 when the payout is not found — untested.
- `SuspendUser`, `ReactivateUser`, `SetUserRole`, `VerifyCreator` 404 paths are untested.
- `GetSettings` returns hardcoded values derived from `TierManifest` — untested.
- `UpdateSettings` returns 501 — untested.
- `[Authorize(Roles = "Admin")]` enforcement: no test verifies a non-admin receives 403.

---

## 3. `RequireCreatorTierAttribute` — no tests at all

**File:** `src/Cambrian.Api/Middleware/RequireCreatorTierAttribute.cs`

This attribute guards the upload and payout routes. It has two code paths:

1. **Fast path** — reads the role from the JWT `ClaimTypes.Role` claim.
2. **DB fallback** — if the JWT has no role claim, fetches the user from `UserManager`.

Neither path is tested. Additionally, `JwtTokenService.CreateToken` (see §4) does **not**
include a `ClaimTypes.Role` claim, meaning the DB fallback executes on **every** protected
request in production. Tests should verify:

- Unauthenticated request → 401
- Authenticated `User` role → 403 with the expected error message
- Authenticated `Creator` role → request proceeds
- Authenticated `Admin` role → request proceeds (admin bypass)
- JWT role claim present (fast path, no DB call)
- JWT role claim absent (DB fallback used)

---

## 4. `JwtTokenService` — latent bug + no tests

**File:** `src/Cambrian.Infrastructure/Security/JwtTokenService.cs`

`CreateToken` generates a JWT with `sub`, `email`, and `display_name` claims but **omits the
`role` claim**. As a result, `RequireCreatorTierAttribute` always takes the DB fallback path,
adding an unnecessary database round-trip to every upload and payout request.

**What should be tested:**

- Token contains `sub` claim equal to `user.Id`
- Token contains `email` claim
- Token contains `display_name` claim
- Token contains a `role` claim (currently missing — fixing this also fixes §3)
- Token expiry matches `JwtOptions.ExpirationMinutes`
- Token validates successfully against the configured key/issuer/audience

---

## 5. `AnalyticsController` — validation logic untested

**File:** `src/Cambrian.Api/Controllers/AnalyticsController.cs`

The controller has an allowlist of valid event types (`play`, `download`, `purchase`,
`search`, `upload`). The request validation path is completely untested.

**What should be tested:**

- Empty `eventType` → 400 with `"eventType is required."`
- Whitespace-only `eventType` → 400
- Disallowed `eventType` (e.g. `"click"`) → 400 listing the allowed values
- Valid `eventType` with a valid `trackId` GUID → 200, analytics recorded
- Valid `eventType` with an invalid / missing `trackId` → 200, recorded with null `trackId`
- `GET /analytics/summary` requires `Admin` role
- `GET /analytics/events` requires `Admin` role

---

## 6. `FeeService` — untested business logic

**File:** `src/Cambrian.Application/Services/FeeService.cs`

Fee rates flow directly into creator payout calculations. Any drift in the manifest values
should be caught by tests.

**What should be tested:**

- `GetPlatformFeeRate(CreatorTier.Free)` returns `0.35` (35 %)
- `GetPlatformFeeRate(CreatorTier.Pro)` returns `0.15` (15 %)
- `GetPlatformFeeRate("free")` (string overload) returns `0.35`
- `GetPlatformFeeRate("pro")` returns `0.15`
- `GetPlatformFeeRate("unknown")` falls back to free tier rate
- `FreeUploadLimit` returns `10`

These are pure functions with no I/O — they can be tested with no mocking.

---

## 7. `WalletController` — exception-to-response mapping untested

**File:** `src/Cambrian.Api/Controllers/WalletController.cs`

`Phase2PaymentTests` tests `WalletService` in isolation, but the controller itself is never
exercised. The `Withdraw` action catches `ArgumentException` and `InvalidOperationException`
and converts them to `ErrorResponse` — this translation layer is untested.

**What should be tested:**

- `GET /wallet` returns balance
- `POST /wallet/withdraw` with positive amount → 200 `{ status: "pending" }`
- `POST /wallet/withdraw` with `ArgumentException` from service → `ErrorResponse`
- `POST /wallet/withdraw` with `InvalidOperationException` (insufficient balance) → `ErrorResponse`
- `GET /wallet/history` with default pagination
- `GET /wallet/history` with `page < 1` clamped to 1
- `GET /wallet/history` with `pageSize > 100` clamped to 20

---

## 8. `StreamController` — no tests at all

**File:** `src/Cambrian.Api/Controllers/StreamController.cs`

**What should be tested:**

- `GET /stream/{trackId}` with invalid GUID → 400
- `GET /stream/{trackId}` with unknown track → 404
- `GET /stream/{trackId}` with valid track → 200 with `streamUrl`
- `GET /stream/{trackId}/audio` with invalid GUID → 400
- `GET /stream/{trackId}/audio` with valid track → 302 redirect to signed URL
- `GET /stream/{trackId}/audio` is `[AllowAnonymous]` — verify no auth required
- `POST /stream/start` with invalid GUID → 400
- `POST /stream/start` with missing track → 404
- `POST /stream/start` with valid track → 200 with `streamId`
- `POST /stream/stop` with invalid GUID → 400
- `POST /stream/stop` with valid GUID → 200

---

## 9. `UsersController` — no tests at all

**File:** `src/Cambrian.Api/Controllers/UsersController.cs`

The `PATCH /users/me` endpoint has non-trivial validation logic:

**What should be tested:**

- `GET /users/{username}` returns public profile + public tracks
- `GET /users/{username}` for unknown user → 404
- `PATCH /users/me` — bio over 500 chars → 400
- `PATCH /users/me` — empty string bio clears the field (sets to `null`)
- `PATCH /users/me` — empty string `profileImageUrl` clears the field
- `PATCH /users/me` — valid update persists changes
- `PATCH /users/me` requires authentication

---

## 10. `FilenameHelper` — untested utility with edge cases

**File:** `src/Cambrian.Api/Common/FilenameHelper.cs`

Used for `Content-Disposition` headers in download responses.

**What should be tested:**

- Normal string passes through unchanged
- String with invalid filename characters (e.g. `"my:track/file"`) has them stripped
- Resulting string that is empty/whitespace-only falls back to `"track"`
- String consisting entirely of invalid characters falls back to `"track"`

These are pure unit tests with no dependencies.

---

## 11. `InvoiceService` — GUID validation and ownership check untested

**File:** `src/Cambrian.Application/Services/InvoiceService.cs`

**What should be tested:**

- `GetByIdAsync` with non-GUID string → returns `null` (no DB call)
- `GetByIdAsync` with valid GUID but wrong user → returns `null` (ownership check)
- `GetByIdAsync` with valid GUID and correct user → returns mapped `InvoiceResponse`
- `GetByUserAsync` maps all fields correctly
- `DownloadAsync` returns `null` (stub — confirms not yet implemented)

---

## 12. Gaps in existing test suites

### `AdminService` — GUID parse shortcut not tested

`ApprovePayoutAsync`, `RejectPayoutAsync`, `RemoveTrackAsync`, `RestoreTrackAsync`,
`HideTrackAsync`, `FlagTrackAsync`, and `SetTrackVisibilityAsync` all silently return
`false` when the ID is not a valid GUID (no DB call made). This silent-failure behaviour
is not exercised.

### `PurchaseService` — concurrent exclusive purchase race not tested

The exclusive-sold guard (`CreateAsync_ThrowsInvalidOperation_WhenTrackExclusiveSold`) uses
a sequential mock. There is no test for the TOCTOU window where two concurrent requests both
pass the initial check. A database-level unique constraint or optimistic concurrency check
should exist and be validated.

### `StripeWebhookService` — copyright buyout webhook path not tested

`StripeWebhookServiceTests` covers standard `checkout.session.completed` for track licenses
and subscriptions. The `copyright_buyout` license type in the client reference is not tested
end-to-end through the webhook handler.

### `AuthService` — `VerifyCodeAsync` and `RecoverUsernameAsync` not tested

`Phase1SecurityTests` covers password reset codes via `AuthService` but not
`RecoverUsernameAsync`. There are also no tests for the `VerifyCodeRequest` endpoint
in `AuthController`.

---

## Recommended prioritisation

| Priority | Area | Reason |
|---|---|---|
| 1 | `ExceptionMiddleware` | Cross-cutting; production message leakage risk |
| 2 | `AdminController` | Sensitive destructive operations; purge guard |
| 3 | `RequireCreatorTierAttribute` | Auth gate for upload/payout |
| 4 | `JwtTokenService` (+ role claim bug) | Fixes latent perf bug + validates token structure |
| 5 | `AnalyticsController` | Input validation allowlist |
| 6 | `FeeService` | Pure functions, zero effort, high financial impact |
| 7 | `WalletController` | Exception mapping |
| 8 | `StreamController` | Anonymous audio access path |
| 9 | `UsersController` | Bio length validation |
| 10 | `FilenameHelper` | Pure function edge cases |
| 11 | `InvoiceService` | Ownership / GUID guards |
| 12 | Gaps in existing suites | Concurrent exclusive purchase, buyout webhook |
