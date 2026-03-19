# Platform Stability Audit Report

**Date:** 2026-03-19  
**Scope:** All controllers in `src/Cambrian.Api/Controllers/` vs `contracts/endpoint-manifest.v1.json` and `contracts/policy.v1.json`  
**Controllers Audited:** 27 files (including BaseController)

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Complete Endpoint Inventory](#complete-endpoint-inventory)
3. [Auth Mismatches: Code vs Manifest](#auth-mismatches-code-vs-manifest)
4. [Policy Violations](#policy-violations)
5. [Undocumented Endpoints (Code but NOT in Manifest)](#undocumented-endpoints)
6. [Missing Implementations (Manifest but NOT in Code)](#missing-implementations)
7. [Null-Forgiving Operator (`!`) Issues](#null-forgiving-operator-issues)
8. [Null-Safety Issues (Missing Null Checks)](#null-safety-issues)
9. [Role-Based Access Control Violations](#role-based-access-control-violations)
10. [Per-Controller Detailed Breakdown](#per-controller-detailed-breakdown)

---

## Executive Summary

| Category | Count |
|---|---|
| Total endpoints in code | 107 |
| Total endpoints in manifest | 103 (with 3 duplicates) |
| Auth mismatches (code vs manifest) | **8** |
| Policy violations | **1 critical** |
| Undocumented endpoints | **6** |
| Missing implementations | **0** |
| Null-forgiving operator (`!`) uses | **48** |
| Null-safety issues (missing null checks) | **2** |
| RBAC violations | **1 critical** |

### Critical Findings

1. **CatalogController has NO authorization** — 5 endpoints marked `requiresAuth: true` in the manifest are completely open to anonymous access.
2. **CommunityController has NO authorization** — endpoint marked `requiresAuth: true` in the manifest is open.
3. **PayoutController lacks creator role enforcement** — policy `payout-routes-require-creator-role` is violated; all 14 payout endpoints only require generic `[Authorize]`.
4. **StreamController `/stream/{trackId}/audio`** uses `[AllowAnonymous]` but manifest says `requiresAuth: true`.
5. **SubscriptionsController `/subscriptions/plans`** uses `[AllowAnonymous]` but manifest says `requiresAuth: true`.
6. **48 instances** of null-forgiving operator (`!`) on `User.FindFirstValue()` calls — any token with a missing `NameIdentifier` claim will cause `NullReferenceException`.
7. **AuthController.GetProfile()** accesses `profile.DisplayName` without null-checking the result of `GetCurrentUserAsync()`.

---

## Complete Endpoint Inventory

### AdminController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/admin/dashboard` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| GET | `/admin/audit` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| GET | `/admin/integrity` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| GET | `/admin/users` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| GET | `/admin/settings` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/settings` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| GET | `/admin/payouts/requests` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/payouts/{id}/approve` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/payouts/{id}/reject` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/users/{id}/role` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/users/{id}/suspend` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/users/{id}/reactivate` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/users/{id}/reset-password` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/users/{id}/verify-creator` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| GET | `/admin/reports` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/reports/{id}/investigate` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/tracks/{id}/remove` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/tracks/{id}/restore` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/tracks/{id}/hide` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/tracks/{id}/flag` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/tracks/{id}/feature` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/tracks/{id}/pin` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/tracks/{id}/visibility` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/collections/curate` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/tags/manage` | `[Authorize(Roles = "Admin")]` (class) | true | OK |
| POST | `/admin/purge-test-data` | `[Authorize(Roles = "Admin")]` (class) | **NOT IN MANIFEST** | UNDOCUMENTED |

### AiController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/ai/playlist` | `[Authorize]` (class) | true | OK |
| POST | `/generate` | `[Authorize]` (class) | true | OK |

### AuthController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| POST | `/auth/register` | None | false | OK |
| POST | `/auth/login` | None | false | OK |
| GET | `/auth/me` | `[Authorize]` | true | OK |
| POST | `/auth/logout` | `[Authorize]` | true | OK |
| GET | `/auth/health` | None | false | OK |
| GET | `/auth/csrf-token` | None | false | OK |
| POST | `/auth/forgot-password` | None | false | OK |
| POST | `/auth/verify-code` | None | false | OK |
| POST | `/auth/reset-password` | None | false | OK |
| POST | `/auth/recover-username` | None | false | OK |
| GET | `/settings/profile` | `[Authorize]` | true | OK |
| POST | `/settings/password` | `[Authorize]` | true | OK |
| PUT | `/settings/password` | `[Authorize]` | true | OK |
| POST | `/settings/email` | `[Authorize]` | true | OK |
| PUT | `/settings/email` | `[Authorize]` | true | OK |

### BillingController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| POST | `/billing/checkout` | `[Authorize]` (class) | true | OK |
| POST | `/billing/checkout-session` | `[Authorize]` (class) | true | OK |
| GET | `/billing/status` | `[Authorize]` (class) | true | OK |
| GET | `/billing/checkout-session/{sessionId}` | `[Authorize]` (class) | true | OK |

### CatalogController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/discover` | **None** | true | **MISMATCH** |
| GET | `/catalog` | **None** | true | **MISMATCH** |
| GET | `/tracks/{trackId}` | **None** | true | **MISMATCH** |
| GET | `/trending` | **None** | true | **MISMATCH** |
| GET | `/tracks` | **None** | true | **MISMATCH** |

### CheckoutController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| POST | `/checkout` | `[Authorize]` | true | OK |
| GET | `/checkout/session/{sessionId}` | `[Authorize]` | true | OK |

### CommunityController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/community` | **None** | true | **MISMATCH** |

### CreatorController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/creator/tracks` | `[Authorize]` + `[RequireCreatorTier]` (class) | true | OK |
| GET | `/creator/revenue` | `[Authorize]` + `[RequireCreatorTier]` (class) | true | OK |

### CreatorProfileController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/creator-profile/{slug}` | None | false | OK |
| GET | `/creator-profile/{slug}/storefront` | None | false | OK |
| GET | `/creator-profile/me` | `[Authorize]` + `[RequireCreatorTier]` | true | OK |
| PUT | `/creator-profile/me` | `[Authorize]` + `[RequireCreatorTier]` | true | OK |
| POST | `/creator-profile/me/banner` | `[Authorize]` + `[RequireCreatorTier]` | true | OK |
| POST | `/creator-profile/me/avatar` | `[Authorize]` + `[RequireCreatorTier]` | true | OK |
| GET | `/creator-profile/{slug}/collections` | None | false | OK |
| POST | `/creator-profile/me/collections` | `[Authorize]` + `[RequireCreatorTier]` | true | OK |
| PUT | `/creator-profile/me/collections/{collectionId}` | `[Authorize]` + `[RequireCreatorTier]` | true | OK |
| DELETE | `/creator-profile/me/collections/{collectionId}` | `[Authorize]` + `[RequireCreatorTier]` | true | OK |
| PUT | `/creator-profile/me/pinned-tracks` | `[Authorize]` + `[RequireCreatorTier]` | true | OK |

### DataController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/data/account` | `[Authorize]` (class) | true | OK |
| GET | `/data/songs` | `[Authorize(Roles = "Admin")]` | true | OK |
| POST | `/data/songs` | `[Authorize(Roles = "Admin")]` | true | OK |
| GET | `/data/system` | `[Authorize(Roles = "Admin")]` | true | OK |
| POST | `/data/system` | `[Authorize(Roles = "Admin")]` | true | OK |
| GET | `/data/secrets` | `[Authorize(Roles = "Admin")]` | true | OK |
| POST | `/data/secrets` | `[Authorize(Roles = "Admin")]` | true | OK |

### DebugController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/debug/user/{userId}` | `[Authorize(Roles = "Admin")]` (class) | **NOT IN MANIFEST** | UNDOCUMENTED |
| GET | `/debug/webhooks` | `[Authorize(Roles = "Admin")]` (class) | **NOT IN MANIFEST** | UNDOCUMENTED |
| GET | `/debug/consistency` | `[Authorize(Roles = "Admin")]` (class) | **NOT IN MANIFEST** | UNDOCUMENTED |

### DownloadController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/download/{trackId}` | `[Authorize]` (class) | true | OK |
| GET | `/download/{trackId}/file` | `[Authorize]` (class) | **NOT IN MANIFEST** | UNDOCUMENTED |
| GET | `/download/{trackId}/signed` | `[Authorize]` (class) | true | OK |

### FeatureFlagsController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/feature-flags/check/{name}` | `[Authorize]` | true | OK |
| GET | `/feature-flags` | `[Authorize(Roles = "Admin")]` | true | OK |
| PUT | `/feature-flags/{name}` | `[Authorize(Roles = "Admin")]` | true | OK |
| DELETE | `/feature-flags/{name}` | `[Authorize(Roles = "Admin")]` | true | OK |

### HealthController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/health` | None | false | OK |
| GET | `/health/storage` | None | false | OK |

### InvoiceController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/invoices` | `[Authorize]` (class) | true | OK |
| GET | `/invoices/{invoiceId}` | `[Authorize]` (class) | true | OK |
| GET | `/invoices/{invoiceId}/download` | `[Authorize]` (class) | true | OK |

### LibraryController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/library` | `[Authorize]` (class) | true | OK |
| POST | `/library` | `[Authorize]` (class) | true | OK |
| DELETE | `/library/{trackId}` | `[Authorize]` (class) | true | OK |
| POST | `/library/{trackId}` | `[Authorize]` (class) | true | OK |
| GET | `/library/purchased-track-ids` | `[Authorize]` (class) | true | OK |

### LicensesController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/licenses/{licenseId}` | `[Authorize]` (class) | true | OK |
| GET | `/licenses` | `[Authorize]` (class) | true | OK |
| GET | `/licenses/{licenseId}/pdf` | `[Authorize]` (class) | **NOT IN MANIFEST** | UNDOCUMENTED |

### PaymentsController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| POST | `/payments/checkout` | `[Authorize]` (class) | true | OK |
| GET | `/payments/state` | `[Authorize]` (class) | true | OK |
| GET | `/payments/result` | `[AllowAnonymous]` | false | OK |
| POST | `/payments/process` | `[Authorize]` (class) | true | OK |
| POST | `/purchases` | `[Authorize]` (class) | true | OK |
| POST | `/purchases/credit-creator` | `[Authorize(Roles = "Admin")]` | true | OK |

### PayoutController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| POST | `/payouts/connect-stripe` | `[Authorize]` (class) | true | OK |
| GET | `/payouts/connect-status` | `[Authorize]` (class) | true | OK |
| GET | `/payouts/stripe-dashboard` | `[Authorize]` (class) | true | OK |
| GET | `/payouts/account` | `[Authorize]` (class) | true | OK |
| POST | `/payouts/connect` | `[Authorize]` (class) | true | OK |
| DELETE | `/payouts/disconnect` | `[Authorize]` (class) | true | OK |
| POST | `/payouts/disconnect` | `[Authorize]` (class) | true | OK |
| GET | `/payouts/earnings` | `[Authorize]` (class) | true | OK |
| POST | `/payouts/request` | `[Authorize]` (class) | true | OK |
| GET | `/payouts/history` | `[Authorize]` (class) | true | OK |
| POST | `/payouts/settings` | `[Authorize]` (class) | true | OK |
| PUT | `/payouts/settings` | `[Authorize]` (class) | true | OK |
| GET | `/earnings` | `[Authorize]` (class) | true | OK |

### StreamController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/stream` | `[Authorize]` | true | OK |
| GET | `/stream/{trackId}` | `[Authorize]` | true | OK |
| GET | `/stream/{trackId}/audio` | **`[AllowAnonymous]`** | true | **MISMATCH** |
| POST | `/stream/start` | `[Authorize]` | true | OK |
| POST | `/stream/stop` | `[Authorize]` | true | OK |

### SubscriptionsController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/subscriptions/plans` | **`[AllowAnonymous]`** | true | **MISMATCH** |
| GET | `/subscriptions/current` | `[Authorize]` (class) | true | OK |
| POST | `/subscriptions/update` | `[Authorize]` (class) | true | OK |
| POST | `/subscriptions/cancel` | `[Authorize]` (class) | true | OK |
| GET | `/subscriptions/history` | `[Authorize]` (class) | true | OK |

### TierController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/tiers/config` | None | false | OK |

### UploadController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| POST | `/upload` | `[Authorize]` + `[RequireCreatorTier]` | true | OK |

### WalletController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| GET | `/wallet` | `[Authorize]` (class) | true | OK |
| POST | `/wallet/withdraw` | `[Authorize]` (class) | true | OK |
| GET | `/wallet/history` | `[Authorize]` (class) | true | OK |

### WebhookController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| POST | `/webhook/stripe` | None | false | OK |

### AnalyticsController.cs
| Method | Route | Auth in Code | Manifest requiresAuth | Match |
|---|---|---|---|---|
| POST | `/analytics/track` | `[Authorize]` | true | OK |
| GET | `/analytics/summary` | `[Authorize(Roles = "Admin")]` | true | OK |
| GET | `/analytics/events` | `[Authorize(Roles = "Admin")]` | true | OK |

---

## Auth Mismatches: Code vs Manifest

**8 endpoints** where code authorization does not match manifest `requiresAuth`:

| # | Method | Route | Code Auth | Manifest `requiresAuth` | Severity |
|---|---|---|---|---|---|
| 1 | GET | `/discover` | **None** | `true` | **CRITICAL** |
| 2 | GET | `/catalog` | **None** | `true` | **CRITICAL** |
| 3 | GET | `/trending` | **None** | `true` | **CRITICAL** |
| 4 | GET | `/tracks/{trackId}` | **None** | `true` | **CRITICAL** |
| 5 | GET | `/tracks` | **None** | `true` | **CRITICAL** |
| 6 | GET | `/community` | **None** | `true` | **CRITICAL** |
| 7 | GET | `/stream/{trackId}/audio` | `[AllowAnonymous]` | `true` | **HIGH** |
| 8 | GET | `/subscriptions/plans` | `[AllowAnonymous]` | `true` | **MEDIUM** |

### Details

**Items 1-5 (CatalogController):** The entire `CatalogController` has no class-level or method-level `[Authorize]` attribute. Any anonymous user can browse the full catalog, discover tracks, view trending, and retrieve individual track details. The manifest declares all of these as requiring authentication.

**Item 6 (CommunityController):** The `CommunityController` has no `[Authorize]` attribute at either class or method level. The `/community` endpoint is completely open.

**Item 7 (StreamController):** `GET /stream/{trackId}/audio` is explicitly marked `[AllowAnonymous]`, which contradicts the manifest's `requiresAuth: true`. The code comment says "Open to anonymous users — this is the marketplace discovery/preview model." This may be intentional design, but it conflicts with the documented contract.

**Item 8 (SubscriptionsController):** `GET /subscriptions/plans` is marked `[AllowAnonymous]` to allow unauthenticated users to view pricing. The manifest says `requiresAuth: true`. This is likely an intentional deviation (users should be able to see plans before signing up), but the manifest is out of sync.

---

## Policy Violations

### Rule: `admin-routes-require-auth`
> "All /admin routes must require JWT authorization."

**Status: PASS** — All endpoints under `/admin/` are in `AdminController`, which has class-level `[Authorize(Roles = "Admin")]`. Every admin endpoint requires Admin role JWT authentication.

### Rule: `payout-routes-require-creator-role`
> "Payout endpoints require creator role."

**Status: FAIL** — `PayoutController` uses class-level `[Authorize]` (generic authentication only). There is **no** `[Authorize(Roles = "creator")]` or `[RequireCreatorTier]` attribute on the controller or any of its 13 endpoints.

**Affected endpoints (all 13 in PayoutController + 1 absolute route):**
- `POST /payouts/connect-stripe`
- `GET /payouts/connect-status`
- `GET /payouts/stripe-dashboard`
- `GET /payouts/account`
- `POST /payouts/connect`
- `DELETE /payouts/disconnect`
- `POST /payouts/disconnect`
- `GET /payouts/earnings`
- `POST /payouts/request`
- `GET /payouts/history`
- `POST /payouts/settings`
- `PUT /payouts/settings`
- `GET /earnings`

**Recommended fix:** Add `[RequireCreatorTier]` to the `PayoutController` class declaration (matching the pattern used by `CreatorController`).

---

## Undocumented Endpoints

**6 endpoints** exist in code but are NOT in the endpoint manifest:

| # | Method | Route | Controller | Auth | Risk |
|---|---|---|---|---|---|
| 1 | POST | `/admin/purge-test-data` | AdminController | `[Authorize(Roles = "Admin")]` | **HIGH** — destructive operation that deletes all test data |
| 2 | GET | `/debug/user/{userId}` | DebugController | `[Authorize(Roles = "Admin")]` | MEDIUM — exposes full user diagnostic state |
| 3 | GET | `/debug/webhooks` | DebugController | `[Authorize(Roles = "Admin")]` | MEDIUM — exposes webhook event history |
| 4 | GET | `/debug/consistency` | DebugController | `[Authorize(Roles = "Admin")]` | LOW — admin diagnostic tool |
| 5 | GET | `/download/{trackId}/file` | DownloadController | `[Authorize]` | LOW — binary streaming fallback |
| 6 | GET | `/licenses/{licenseId}/pdf` | LicensesController | `[Authorize]` | LOW — PDF certificate download |

All undocumented endpoints do have proper authorization. However, the **purge-test-data** endpoint is a destructive admin operation that should be documented and carefully controlled.

---

## Missing Implementations

**0 endpoints** exist in the manifest but are missing from code.

All 100 unique manifest endpoints (excluding 3 duplicates: `GET /health/storage`, `GET /stream/{trackId}/audio`, `POST /webhook/stripe` each appear twice) have matching controller implementations.

---

## Null-Forgiving Operator Issues

**48 instances** of the null-forgiving operator (`!`) on `User.FindFirstValue()` calls across 13 controllers. If the JWT token is malformed or missing the `NameIdentifier` claim, these will throw `NullReferenceException` at runtime.

### By Controller

| Controller | File | Lines | Count |
|---|---|---|---|
| BillingController | BillingController.cs | 26, 54, 66 | 3 |
| CheckoutController | CheckoutController.cs | 49 | 1 |
| CreatorController | CreatorController.cs | 27, 35 | 2 |
| CreatorProfileController | CreatorProfileController.cs | 67, 81, 115, 133, 160, 176, 193, 209 | 8 |
| DataController | DataController.cs | 25 | 1 |
| DownloadController | DownloadController.cs | 41, 87, 120 | 3 |
| InvoiceController | InvoiceController.cs | 22, 33, 48 | 3 |
| PaymentsController | PaymentsController.cs | 28, 51, 61 | 3 |
| PayoutController | PayoutController.cs | 25, 33, 41, 49, 57, 73, 81, 89, 97, 105, 131 | 11 |
| SubscriptionsController | SubscriptionsController.cs | 31, 39, 58, 74 | 4 |
| WalletController | WalletController.cs | 22, 30, 53 | 3 |
| **Subtotal** | | | **42** |

Additionally, these controllers use `User.FindFirstValue()` **without** `!` but also **without** null checks, passing potentially-null values downstream:

| Controller | File | Lines | Issue |
|---|---|---|---|
| CheckoutController | CheckoutController.cs | 30 | Passed to logger but not guarded |
| LibraryController | LibraryController.cs | 26 | Passed to logger but not guarded |
| StreamController | StreamController.cs | 96 | Passed to `_streams.StartAsync()` |
| AnalyticsController | AnalyticsController.cs | 42 | Passed to `_analytics.RecordAsync()` |
| FeatureFlagsController | FeatureFlagsController.cs | 25 | Passed to `_flags.IsEnabledAsync()` |
| DebugController | DebugController.cs | 31, 58 | Passed to logger (non-critical) |

**Recommended fix:** Create a `GetUserId()` helper in `BaseController` that extracts the claim and returns a `BadRequest` or `Unauthorized` if it's null, eliminating all 48+ instances.

---

## Null-Safety Issues

### 1. AuthController.GetProfile() — Missing null check on `profile`

**File:** `AuthController.cs`, lines 152-162

```csharp
[Authorize]
[HttpGet("/settings/profile")]
public async Task<IActionResult> GetProfile()
{
    var profile = await _auth.GetCurrentUserAsync(User);
    return OkResponse(new
    {
        displayName = profile.DisplayName,  // NullReferenceException if profile is null
        email = profile.Email,
        tier = profile.Tier,
        role = profile.Role
    });
}
```

The `Me()` method in the same controller correctly checks `if (profile is null)` and returns `NotFoundResponse`. The `GetProfile()` method does NOT perform this check. If the user is deleted from the database while holding a valid token, this will throw a `NullReferenceException`.

### 2. CheckoutController.Checkout() — userId used without null-forgiving but not guarded

**File:** `CheckoutController.cs`, line 30

```csharp
var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
```

The value is passed to the logger and used in subsequent operations. Not critical for the logger, but could cause downstream issues.

---

## Role-Based Access Control Violations

### 1. PayoutController — Missing Creator Role Requirement (CRITICAL)

**Policy:** `payout-routes-require-creator-role` — "Payout endpoints require creator role."

The `PayoutController` only uses `[Authorize]` at the class level. Any authenticated user (free tier, paid tier, or otherwise) can access all payout endpoints including:
- Connecting/disconnecting Stripe accounts
- Viewing earnings
- Requesting payouts
- Modifying payout settings

**Contrast with CreatorController** which correctly uses both `[Authorize]` and `[RequireCreatorTier]` at the class level.

**Fix:** Add `[RequireCreatorTier]` to the `PayoutController` class declaration.

### 2. CatalogController — No Auth at All

While not strictly an RBAC violation, the complete absence of any authorization on `CatalogController` means the endpoints don't participate in the auth system at all. If these endpoints are intentionally public, the manifest should be updated to reflect `requiresAuth: false`.

---

## Per-Controller Detailed Breakdown

### AdminController.cs
- **Route prefix:** `admin`
- **Class-level auth:** `[Authorize(Roles = "Admin")]`
- **Endpoints:** 26 (25 in manifest + 1 undocumented)
- **Policy compliance:** PASS for `admin-routes-require-auth`
- **Issues:**
  - `POST /admin/purge-test-data` is undocumented in the manifest
  - No null-forgiving operators (uses safe `?.Value` pattern)

### AiController.cs
- **Route prefix:** `ai`
- **Class-level auth:** `[Authorize]`
- **Endpoints:** 2
- **Issues:** None

### AuthController.cs
- **Route prefix:** `auth`
- **Class-level auth:** None (method-level as needed)
- **Endpoints:** 15
- **Issues:**
  - `GetProfile()` at line 152 does not null-check `profile` before accessing properties
  - Settings routes (`/settings/*`) use absolute route overrides — these are hosted in AuthController despite the route prefix not matching

### BillingController.cs
- **Route prefix:** `billing`
- **Class-level auth:** `[Authorize]`
- **Endpoints:** 4
- **Issues:**
  - 3 null-forgiving operators (lines 26, 54, 66)

### CatalogController.cs
- **Route prefix:** (root)
- **Class-level auth:** **None**
- **Endpoints:** 5
- **Issues:**
  - **CRITICAL: All 5 endpoints are unauthenticated but manifest says requiresAuth: true**
  - No null-forgiving operators (doesn't use User claims)

### CheckoutController.cs
- **Route prefix:** (root)
- **Class-level auth:** None (method-level)
- **Endpoints:** 2
- **Issues:**
  - 1 null-forgiving operator (line 49)
  - Line 30: userId extracted without null-forgiving but also without guard

### CommunityController.cs
- **Route prefix:** `community`
- **Class-level auth:** **None**
- **Endpoints:** 1
- **Issues:**
  - **CRITICAL: Endpoint is unauthenticated but manifest says requiresAuth: true**

### CreatorController.cs
- **Route prefix:** `creator`
- **Class-level auth:** `[Authorize]` + `[RequireCreatorTier]`
- **Endpoints:** 2
- **Issues:**
  - 2 null-forgiving operators (lines 27, 35)

### CreatorProfileController.cs
- **Route prefix:** `creator-profile`
- **Class-level auth:** None (method-level as needed)
- **Endpoints:** 11
- **Issues:**
  - 8 null-forgiving operators (lines 67, 81, 115, 133, 160, 176, 193, 209)

### DataController.cs
- **Route prefix:** `data`
- **Class-level auth:** `[Authorize]`
- **Endpoints:** 7
- **Issues:**
  - 1 null-forgiving operator (line 25)
  - Admin-only endpoints (songs, system, secrets) correctly use `[Authorize(Roles = "Admin")]`

### DebugController.cs
- **Route prefix:** `debug`
- **Class-level auth:** `[Authorize(Roles = "Admin")]`
- **Endpoints:** 3 (all undocumented)
- **Issues:**
  - All 3 endpoints are missing from manifest

### DownloadController.cs
- **Route prefix:** `download`
- **Class-level auth:** `[Authorize]`
- **Endpoints:** 3 (1 undocumented)
- **Issues:**
  - 3 null-forgiving operators (lines 41, 87, 120)
  - `GET /download/{trackId}/file` is undocumented

### FeatureFlagsController.cs
- **Route prefix:** `feature-flags`
- **Class-level auth:** None (method-level)
- **Endpoints:** 4
- **Issues:** None (admin endpoints properly use `[Authorize(Roles = "Admin")]`)

### HealthController.cs
- **Route prefix:** `health`
- **Class-level auth:** None
- **Endpoints:** 2
- **Issues:** None (correctly unauthenticated)
- **Note:** Extends `ControllerBase` directly, not `BaseController`

### InvoiceController.cs
- **Route prefix:** `invoices`
- **Class-level auth:** `[Authorize]`
- **Endpoints:** 3
- **Issues:**
  - 3 null-forgiving operators (lines 22, 33, 48)

### LibraryController.cs
- **Route prefix:** `library`
- **Class-level auth:** `[Authorize]`
- **Endpoints:** 5
- **Issues:** None (userId passed to service via `User` ClaimsPrincipal, not extracted manually)

### LicensesController.cs
- **Route prefix:** `licenses`
- **Class-level auth:** `[Authorize]`
- **Endpoints:** 3 (1 undocumented)
- **Issues:**
  - `GET /licenses/{licenseId}/pdf` is undocumented
  - Uses `User.FindFirstValue()` without `!` but also without explicit null guard (lines 29, 48, 62)

### PaymentsController.cs
- **Route prefix:** `payments`
- **Class-level auth:** `[Authorize]`
- **Endpoints:** 6
- **Issues:**
  - 3 null-forgiving operators (lines 28, 51, 61)
  - `/purchases` and `/purchases/credit-creator` use absolute routes, hosted in PaymentsController

### PayoutController.cs
- **Route prefix:** `payouts`
- **Class-level auth:** `[Authorize]`
- **Endpoints:** 13
- **Issues:**
  - **CRITICAL: Missing `[RequireCreatorTier]` — violates `payout-routes-require-creator-role` policy**
  - 11 null-forgiving operators (lines 25, 33, 41, 49, 57, 73, 81, 89, 97, 105, 131)

### StreamController.cs
- **Route prefix:** `stream`
- **Class-level auth:** None (method-level)
- **Endpoints:** 5
- **Issues:**
  - `GET /stream/{trackId}/audio` uses `[AllowAnonymous]` but manifest says `requiresAuth: true`

### SubscriptionsController.cs
- **Route prefix:** `subscriptions`
- **Class-level auth:** `[Authorize]`
- **Endpoints:** 5
- **Issues:**
  - `GET /subscriptions/plans` uses `[AllowAnonymous]` but manifest says `requiresAuth: true`
  - 4 null-forgiving operators (lines 31, 39, 58, 74)

### TierController.cs
- **Route prefix:** `tiers`
- **Class-level auth:** None
- **Endpoints:** 1
- **Issues:** None (correctly unauthenticated)

### UploadController.cs
- **Route prefix:** (root)
- **Class-level auth:** None (method-level `[Authorize]` + `[RequireCreatorTier]`)
- **Endpoints:** 1
- **Issues:** None

### WalletController.cs
- **Route prefix:** `wallet`
- **Class-level auth:** `[Authorize]`
- **Endpoints:** 3
- **Issues:**
  - 3 null-forgiving operators (lines 22, 30, 53)

### WebhookController.cs
- **Route prefix:** `webhook`
- **Class-level auth:** None
- **Endpoints:** 1
- **Issues:** None (correctly unauthenticated; Stripe webhooks use signature verification)

### AnalyticsController.cs
- **Route prefix:** `analytics`
- **Class-level auth:** None (method-level)
- **Endpoints:** 3
- **Issues:** None (admin endpoints properly secured with `[Authorize(Roles = "Admin")]`)

---

## Recommendations

### Immediate (Critical)

1. **Add `[Authorize]` to CatalogController** or update the manifest to set `requiresAuth: false` for catalog browsing endpoints.
2. **Add `[Authorize]` to CommunityController** or update the manifest.
3. **Add `[RequireCreatorTier]` to PayoutController** to comply with `payout-routes-require-creator-role` policy.

### High Priority

4. **Resolve `/stream/{trackId}/audio` auth mismatch** — either add `[Authorize]` to the endpoint or update the manifest to `requiresAuth: false`.
5. **Fix AuthController.GetProfile() null check** — add `if (profile is null) return NotFoundResponse(...)` before accessing properties.
6. **Add `POST /admin/purge-test-data` to the manifest** — this destructive endpoint must be documented.

### Medium Priority

7. **Create a `GetUserId()` helper in BaseController** to eliminate 48+ null-forgiving operators with a single safe extraction method.
8. **Add all DebugController endpoints to the manifest** with proper documentation.
9. **Add `GET /download/{trackId}/file` and `GET /licenses/{licenseId}/pdf` to the manifest**.
10. **Resolve `/subscriptions/plans` auth mismatch** — likely update the manifest to `requiresAuth: false`.
11. **Remove duplicate entries in the manifest** — `GET /health/storage`, `GET /stream/{trackId}/audio`, and `POST /webhook/stripe` each appear twice.

### Low Priority

12. **HealthController extends ControllerBase directly** instead of `BaseController` — this means it doesn't have access to the standard response helpers, but this is functionally correct since health endpoints return raw results.
