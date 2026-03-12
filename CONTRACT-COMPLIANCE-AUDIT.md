# Cambrian Contract Compliance Audit Report

**Date:** 2026-03-12  
**Contract:** `contracts/openapi.v1.json` (OpenAPI 3.0.1)  
**Scope:** Backend controllers, Application DTOs, Domain enums  
**Note:** No frontend or generated API client exists in this repository. Those layers are in a separate project and could not be audited.

---

## Executive Summary

| Category | Count |
|---|---|
| Critical violations | 7 |
| Major violations | 8 |
| Minor violations | 6 |
| Undocumented endpoints | 5 |
| Manifest inconsistencies | 3 |
| **Total findings** | **29** |

---

## Section 1: Undocumented Endpoints (Backend exists, Contract missing)

These endpoints exist in the backend but are NOT defined in `contracts/openapi.v1.json`.

### UE-01: `GET /checkout/session/{sessionId}`

| Field | Value |
|---|---|
| **Location** | `CheckoutController.ConfirmSession()` |
| **Controller** | `src/Cambrian.Api/Controllers/CheckoutController.cs` |
| **Severity** | Major |
| **Issue** | Endpoint exists in backend but has no contract definition. The billing equivalent `GET /billing/checkout-session/{sessionId}` IS in the contract. |
| **Correction** | Either add to contract or remove from backend. If this duplicates the billing endpoint, remove from `CheckoutController`. |

### UE-02: `GET /stream/{trackId}/audio`

| Field | Value |
|---|---|
| **Location** | `StreamController.StreamAudio()` |
| **Controller** | `src/Cambrian.Api/Controllers/StreamController.cs` |
| **Severity** | Major |
| **Issue** | Audio streaming proxy endpoint is not documented in the contract. Frontend `<audio>` elements point at this endpoint. |
| **Correction** | Add `GET /stream/{trackId}/audio` to the OpenAPI spec with `produces: audio/mpeg` and range-request support documentation. |

### UE-03: `GET /health/storage`

| Field | Value |
|---|---|
| **Location** | `HealthController.StorageDiag()` |
| **Controller** | `src/Cambrian.Api/Controllers/HealthController.cs` |
| **Severity** | Minor |
| **Issue** | Diagnostic endpoint not in contract. |
| **Correction** | Add to contract under the `Health` tag, or document as internal-only and exclude from public API. |

### UE-04: `GET /licenses`

| Field | Value |
|---|---|
| **Location** | `LicensesController.ListMyLicenses()` |
| **Controller** | `src/Cambrian.Api/Controllers/LicensesController.cs` |
| **Severity** | Major |
| **Issue** | Lists all licenses for the authenticated user. Not in contract. |
| **Correction** | Add `GET /licenses` to the OpenAPI spec returning `array of LicenseCertificate`. |

### UE-05: `POST /billing/checkout-session`

| Field | Value |
|---|---|
| **Location** | `BillingController.CheckoutSession()` |
| **Controller** | `src/Cambrian.Api/Controllers/BillingController.cs` |
| **Severity** | Minor |
| **Issue** | This endpoint IS in the OpenAPI spec but is MISSING from `endpoint-manifest.v1.json`. The backend implementation exists. |
| **Correction** | Add to `endpoint-manifest.v1.json`. |

---

## Section 2: Response Envelope Violations

### ENV-01: Auth endpoints bypass `ApiResponse` envelope (CRITICAL)

| Field | Value |
|---|---|
| **Violation** | `/auth/register`, `/auth/login`, `/auth/me` return raw objects instead of `ApiResponseOfAuthSession` |
| **Location** | `src/Cambrian.Api/Controllers/AuthController.cs` |
| **Expected (contract)** | `{ "success": true, "data": { "token": "...", "tier": "...", "user": { ... } }, "message": null, "error": null }` |
| **Actual (backend)** | `{ "token": "...", "tier": "...", "user": { ... } }` |
| **Details** | The `Register()` method calls `StatusCode(201, ToSession(result))` and `Login()` calls `Ok(ToSession(result))` — both bypass the `OkResponse()` helper. The `Me()` action returns `Ok(new { token, user })` directly. |
| **Correction** | Replace `Ok(ToSession(result))` with `OkResponse(ToSession(result))` in `Login`. Replace `StatusCode(201, ToSession(result))` with `CreatedResponse(ToSession(result))` in `Register`. Replace the raw return in `Me()` with `OkResponse(new { token, tier, user })`. |

### ENV-02: Most non-Auth responses wrapped but contract expects unwrapped (CRITICAL)

| Field | Value |
|---|---|
| **Violation** | Backend wraps ALL responses in `{ success, data, message, error }` via `BaseController.OkResponse()`, but the contract schemas do not define this envelope for most endpoints. |
| **Location** | All controllers except Auth (which has the inverse problem) |
| **Affected endpoints** | All `/admin/*`, `/catalog`, `/discover`, `/trending`, `/checkout`, `/payments/*`, `/library/*`, `/invoices/*`, `/wallet/*`, `/stream/*`, `/subscriptions/*`, `/payouts/*`, `/licenses/*`, etc. |
| **Expected (contract)** | Direct schema, e.g. for `GET /admin/dashboard`: `AdminDashboardSummary` object |
| **Actual (backend)** | `{ "success": true, "data": { <AdminDashboardSummary> }, "message": null, "error": null }` |
| **Correction** | **Option A (recommended):** Update the OpenAPI spec to define a generic `ApiResponse<T>` wrapper and use it consistently for all endpoints. **Option B:** Remove the envelope from backend responses. Option A is recommended since the envelope is already implemented system-wide. |

---

## Section 3: Request DTO Violations

### REQ-01: `RegisterRequest` missing `plan` and `phoneNumber` fields

| Field | Value |
|---|---|
| **Violation** | Contract defines `plan` (string, nullable) and `phoneNumber` (string, nullable, pattern `^\+[1-9]\d{6,14}$`) but they are absent from the backend DTO. |
| **Location** | `src/Cambrian.Application/DTOs/Auth/RegisterRequest.cs` |
| **Contract schema** | `RegisterRequest` with properties `plan`, `phoneNumber` |
| **Backend DTO** | Missing both fields |
| **Correction** | Add to `RegisterRequest.cs`: `public string? Plan { get; set; }` and `[RegularExpression(@"^\+[1-9]\d{6,14}$")] public string? PhoneNumber { get; set; }` |

### REQ-02: `RegisterRequest.Password` missing regex pattern validation

| Field | Value |
|---|---|
| **Violation** | Contract requires pattern `^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[\W_]).{8,}$` on password. Backend only has `[MinLength(8)]`. |
| **Location** | `src/Cambrian.Application/DTOs/Auth/RegisterRequest.cs` |
| **Contract schema** | `password: { minLength: 8, pattern: "^(?=.*[a-z])(?=.*[A-Z])(?=.*\\d)(?=.*[\\W_]).{8,}$" }` |
| **Backend DTO** | `[MinLength(8)]` only |
| **Correction** | Add `[RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[\W_]).{8,}$", ErrorMessage = "Password must contain uppercase, lowercase, digit, and special character.")]` |

### REQ-03: `CheckoutRequest.TrackId` type mismatch and `LicenseType` required mismatch

| Field | Value |
|---|---|
| **Violation** | (a) Contract defines `trackId` as `format: uuid` — backend has no UUID validation. (b) Contract defines `licenseType` as nullable (not required) — backend marks it `[Required]`. (c) Backend regex allows `standard|non-exclusive|exclusive` but contract schema does not constrain `licenseType` at all. |
| **Location** | `src/Cambrian.Application/DTOs/Checkout/CheckoutRequest.cs` |
| **Correction** | (a) Add `[RegularExpression(@"^[0-9a-fA-F\-]{36}$")]` or a custom UUID validator. (b) Remove `[Required]` from `LicenseType`. (c) Keep regex validation but note the discrepancy. |

### REQ-04: `PurchaseCreateRequest` missing `amount` field, extra `StripeSessionId`

| Field | Value |
|---|---|
| **Violation** | Contract defines `amount` (number, double) — missing from DTO. Backend has `StripeSessionId` not defined in contract. |
| **Location** | `src/Cambrian.Application/DTOs/Purchases/PurchaseCreateRequest.cs` |
| **Contract schema** | `{ trackId (required), amount (number), paymentMethod, licenseType, usageType }` |
| **Backend DTO** | `{ TrackId, LicenseType, PaymentMethod, StripeSessionId, UsageType }` |
| **Correction** | Add `public double Amount { get; set; }`. Either remove `StripeSessionId` or add it to the contract. |

### REQ-05: `CreditCreatorRequest.AmountCents` name and type mismatch

| Field | Value |
|---|---|
| **Violation** | Contract field is `amount` (number, double). Backend field is `AmountCents` (int). Both the name (serialized as `amountCents` vs `amount`) and type differ. |
| **Location** | `src/Cambrian.Application/DTOs/Purchases/CreditCreatorRequest.cs` |
| **Contract schema** | `amount: { type: "number", format: "double" }` |
| **Backend DTO** | `public int AmountCents { get; set; }` |
| **Correction** | Rename to `Amount` and change type to `double`, or update the contract to `amountCents` (integer). Must be coordinated with frontend. |

### REQ-06: `PaymentProcessRequest` missing deprecated card fields

| Field | Value |
|---|---|
| **Violation** | Contract defines `cardNumber`, `cardExpiry`, `cardCvc`, `cardName` (all deprecated, nullable). Backend DTO omits them. |
| **Location** | `src/Cambrian.Application/DTOs/Payments/PaymentProcessRequest.cs` |
| **Contract schema** | Includes `cardNumber`, `cardExpiry`, `cardCvc`, `cardName` (deprecated) |
| **Backend DTO** | Only `PurchaseId` and `PaymentMethodId` |
| **Correction** | Since these are deprecated, either: (a) add them as `[Obsolete] public string? CardNumber { get; set; }` etc., or (b) remove them from the contract. Option (b) preferred since card data should never touch the server. |

### REQ-07: `/payouts/connect` request body schema mismatch

| Field | Value |
|---|---|
| **Violation** | Contract defines `ConnectBankRequest` with `provider` (enum: ["stripe"]). Backend uses inline `PayoutConnectRequest` with completely different fields: `PlaidPublicToken`, `AccountId`, `AccountHolderName`, `AccountType`. |
| **Location** | `src/Cambrian.Api/Controllers/PayoutController.cs` (inline class) |
| **Contract schema** | `{ provider: "stripe" }` |
| **Backend** | `{ plaidPublicToken, accountId, accountHolderName, accountType }` |
| **Correction** | Backend should accept `ConnectBankRequest` (or update the contract to match the inline class). Since the backend calls `_connect.StartOnboardingAsync(userId)` and ignores the body, the simplest fix is to change the parameter to `ConnectBankRequest?`. |

### REQ-08: `/tracks/upload` missing request body

| Field | Value |
|---|---|
| **Violation** | Contract defines request body as `UploadTrackPricingRequest` (with `title`, `nonExclusivePriceCents`, `exclusivePriceCents`). Backend accepts no body. |
| **Location** | `src/Cambrian.Api/Controllers/CatalogController.cs` → `TracksUpload()` |
| **Contract schema** | `UploadTrackPricingRequest { title (required), nonExclusivePriceCents (int), exclusivePriceCents (int) }` |
| **Backend** | `public IActionResult TracksUpload() { return CreatedResponse<object?>(null, ...); }` — no parameters |
| **Correction** | Add parameter: `TracksUpload([FromBody] UploadTrackPricingRequest request)` and create or use the corresponding DTO. |

---

## Section 4: Response DTO / Schema Violations

### RES-01: `/checkout` response missing `licenseCertificate`, has extra `status`

| Field | Value |
|---|---|
| **Violation** | Contract response: `{ checkoutUrl, licenseCertificate }`. Backend returns: `{ checkoutUrl, status }`. |
| **Location** | `src/Cambrian.Api/Controllers/CheckoutController.cs` → `Checkout()` |
| **Contract schema** | `{ checkoutUrl: string, licenseCertificate: LicenseCertificate (nullable) }` |
| **Backend** | `new { checkoutUrl = session.CheckoutUrl, status = session.Status }` |
| **Correction** | Return `new { checkoutUrl = session.CheckoutUrl, licenseCertificate = session.LicenseCertificate }` matching the contract. The `CheckoutResponse` DTO already has a `LicenseCertificate` property. |

### RES-02: `/upload` request DTO has extra `CoverArt` field

| Field | Value |
|---|---|
| **Violation** | Backend `UploadTrackRequest` has a `CoverArt` (IFormFile) property not defined in the contract's multipart schema. |
| **Location** | `src/Cambrian.Application/DTOs/Catalog/UploadTrackRequest.cs` |
| **Contract** | No `CoverArt` field in the upload schema |
| **Backend** | `public IFormFile? CoverArt { get; set; }` |
| **Correction** | Add `CoverArt` to the contract's `/upload` multipart schema as `{ type: "string", format: "binary" }`, or remove from DTO. |

---

## Section 5: Query Parameter Violations

### QP-01: `GET /community` parameter name mismatch

| Field | Value |
|---|---|
| **Violation** | Contract defines `take` (integer, default 20). Backend uses `page` and `pageSize`. |
| **Location** | `src/Cambrian.Api/Controllers/CommunityController.cs` |
| **Contract** | `?take=20` |
| **Backend** | `?page=1&pageSize=20` |
| **Correction** | Change backend to accept `[FromQuery] int take = 20` matching the contract, or update the contract to use `page`/`pageSize`. |

### QP-02: `GET /wallet/history` parameter name mismatch

| Field | Value |
|---|---|
| **Violation** | Contract defines `take` (integer, default 50). Backend uses `page` and `pageSize`. |
| **Location** | `src/Cambrian.Api/Controllers/WalletController.cs` → `History()` |
| **Contract** | `?take=50` |
| **Backend** | `?page=1&pageSize=20` |
| **Correction** | Change backend parameter to `[FromQuery] int take = 50` matching the contract. |

---

## Section 6: Route / Parameter Violations

### RT-01: `GET /licenses/{trackId}` route parameter name mismatch

| Field | Value |
|---|---|
| **Violation** | Contract defines `GET /licenses/{trackId}` with parameter name `trackId` and pattern `^CAMB-TRK-[A-Z0-9]{4,12}$`. Backend defines `GET /licenses/{licenseId}` with parameter name `licenseId` and no pattern. |
| **Location** | `src/Cambrian.Api/Controllers/LicensesController.cs` → `GetLicense()` |
| **Contract** | Path parameter `trackId` with pattern validation, summary "Retrieve the license certificate for a purchased track" |
| **Backend** | Path parameter `licenseId`, retrieves by license ID instead of track ID |
| **Correction** | Rename route to `[HttpGet("{trackId}")]`, rename parameter to `trackId`, change service call to look up by trackId (for the current user), and add pattern validation. |

### RT-02: Duplicate route definition `/tracks/{trackId}` vs `/tracks/{id}`

| Field | Value |
|---|---|
| **Violation** | The contract defines BOTH `/tracks/{trackId}` (tag: Catalog) and `/tracks/{id}` (tag: Tracks). These resolve to the same route at runtime and would conflict. |
| **Location** | `contracts/openapi.v1.json` lines 1366 and 2727 |
| **Contract** | Two separate path entries for the same resource |
| **Backend** | CatalogController handles `/tracks/{trackId}` |
| **Correction** | Remove the duplicate `/tracks/{id}` entry from the contract, or consolidate into a single entry. |

### RT-03: `/stream/stop` accepts `streamId` as query param, contract defines it in request body

| Field | Value |
|---|---|
| **Violation** | Contract defines `StreamStopRequest` body with `streamId` field. Backend reads `streamId` from query parameter `[FromQuery]`. |
| **Location** | `src/Cambrian.Api/Controllers/StreamController.cs` → `Stop()` |
| **Contract** | `requestBody: { schema: StreamStopRequest { streamId: string } }` |
| **Backend** | `Stop([FromQuery] string? streamId)` |
| **Correction** | Change to `Stop([FromBody] StreamStopRequest? request)` and read `streamId` from `request.StreamId`. |

---

## Section 7: Endpoint Manifest Inconsistencies

### MAN-01: `POST /subscriptions/cancel` missing from manifest

Present in `openapi.v1.json` and implemented in `SubscriptionsController`, but absent from `endpoint-manifest.v1.json`.

### MAN-02: `GET /subscriptions/history` missing from manifest

Present in `openapi.v1.json` and implemented in `SubscriptionsController`, but absent from `endpoint-manifest.v1.json`.

### MAN-03: `POST /billing/checkout-session` missing from manifest

Present in `openapi.v1.json` and implemented in `BillingController`, but absent from `endpoint-manifest.v1.json`.

---

## Section 8: Minor Validation Discrepancies

### VAL-01: `LoginRequest.Password` minLength mismatch

| Field | Value |
|---|---|
| **Contract** | `minLength: 1` |
| **Backend** | `[MinLength(6)]` |
| **Impact** | Backend is stricter than contract — passwords 1-5 chars rejected by backend but allowed by contract |
| **Correction** | Change `[MinLength(6)]` to `[MinLength(1)]` in `LoginRequest.cs`, or update contract to `minLength: 6`. |

### VAL-02: `ChangePasswordRequest`/`ChangeEmailRequest` vs contract `UpdatePasswordRequest`/`UpdateEmailRequest`

| Field | Value |
|---|---|
| **Contract** | Fields are nullable (not required) |
| **Backend** | Both `CurrentPassword`/`NewPassword` and `Password`/`NewEmail` are `[Required]` |
| **Impact** | Backend is stricter — rejects null values that contract permits |
| **Correction** | Either remove `[Required]` from DTOs or add `required` to contract schemas. Backend strictness is preferred, so update the contract. |

### VAL-03: `UploadTrackRequest.LicenseType` regex pattern mismatch

| Field | Value |
|---|---|
| **Contract** | Pattern `^(streaming|personal|commercial|exclusive)$` |
| **Backend** | No regex validation on `LicenseType` property |
| **Correction** | Add `[RegularExpression(@"^(streaming|personal|commercial|exclusive)$")]` to `UploadTrackRequest.LicenseType`. |

---

## Section 9: Compliance Matrix

### All Contract Endpoints vs Backend Implementation

| Method | Path | Tag | Backend Controller | Status |
|---|---|---|---|---|
| GET | /admin/audit | Admin | AdminController | OK |
| GET | /admin/dashboard | Admin | AdminController | OK |
| GET | /admin/settings | Admin | AdminController | OK |
| POST | /admin/settings | Admin | AdminController | OK |
| GET | /admin/payouts/requests | Admin | AdminController | OK |
| POST | /admin/payouts/{id}/approve | Admin | AdminController | OK |
| POST | /admin/payouts/{id}/reject | Admin | AdminController | OK |
| POST | /admin/users/{id}/role | Admin | AdminController | OK |
| GET | /admin/users | Admin | AdminController | OK |
| POST | /admin/users/{id}/suspend | Admin | AdminController | OK |
| POST | /admin/users/{id}/reactivate | Admin | AdminController | OK |
| POST | /admin/users/{id}/reset-password | Admin | AdminController | OK |
| POST | /admin/users/{id}/verify-creator | Admin | AdminController | OK |
| GET | /admin/reports | Admin | AdminController | OK |
| POST | /admin/reports/{id}/investigate | Admin | AdminController | OK |
| POST | /admin/tracks/{id}/remove | Admin | AdminController | OK |
| POST | /admin/tracks/{id}/restore | Admin | AdminController | OK |
| POST | /admin/tracks/{id}/hide | Admin | AdminController | OK |
| POST | /admin/tracks/{id}/flag | Admin | AdminController | OK |
| POST | /admin/tracks/{id}/feature | Admin | AdminController | OK |
| POST | /admin/tracks/{id}/pin | Admin | AdminController | OK |
| POST | /admin/tracks/{id}/visibility | Admin | AdminController | OK |
| POST | /admin/collections/curate | Admin | AdminController | OK |
| POST | /admin/tags/manage | Admin | AdminController | OK |
| GET | /admin/integrity | Admin | AdminController | OK |
| GET | /ai/playlist | Ai | AiController | OK |
| POST | /generate | Ai | AiController | OK |
| GET | /creator/tracks | Ai | CreatorController | OK |
| GET | /creator/revenue | Ai | CreatorController | OK |
| GET | /auth/health | Auth | AuthController | OK |
| POST | /auth/register | Auth | AuthController | **ENV-01** |
| POST | /auth/login | Auth | AuthController | **ENV-01** |
| POST | /auth/logout | Auth | AuthController | OK |
| GET | /auth/me | Auth | AuthController | **ENV-01** |
| GET | /auth/csrf-token | Auth | AuthController | OK |
| POST | /auth/forgot-password | Auth | AuthController | OK |
| POST | /auth/verify-code | Auth | AuthController | OK |
| POST | /auth/reset-password | Auth | AuthController | OK |
| POST | /auth/recover-username | Auth | AuthController | OK |
| POST | /billing/checkout | Billing | BillingController | OK |
| GET | /billing/status | Billing | BillingController | OK |
| POST | /billing/checkout-session | Billing | BillingController | OK |
| GET | /billing/checkout-session/{sessionId} | Billing | BillingController | OK |
| GET | /discover | Catalog | CatalogController | OK |
| GET | /catalog | Catalog | CatalogController | OK |
| GET | /trending | Catalog | CatalogController | OK |
| GET | /tracks/{trackId} | Catalog | CatalogController | OK |
| POST | /upload | Catalog | UploadController | **RES-02** |
| POST | /checkout | Checkout | CheckoutController | **RES-01** |
| GET | /earnings | Payouts | PayoutController | OK |
| GET | /community | Community | CommunityController | **QP-01** |
| GET | /data/account | Data | DataController | OK |
| GET | /data/songs | Data | DataController | OK |
| POST | /data/songs | Data | DataController | OK |
| GET | /data/system | Data | DataController | OK |
| POST | /data/system | Data | DataController | OK |
| GET | /data/secrets | Data | DataController | OK |
| POST | /data/secrets | Data | DataController | OK |
| GET | /health | Health | HealthController | OK |
| GET | /invoices | Invoices | InvoiceController | OK |
| GET | /invoices/{invoiceId} | Invoices | InvoiceController | OK |
| GET | /invoices/{invoiceId}/download | Invoices | InvoiceController | OK |
| GET | /library | Library | LibraryController | OK |
| POST | /library | Library | LibraryController | OK |
| GET | /library/purchased-track-ids | Library | LibraryController | OK |
| DELETE | /library/{trackId} | Library | LibraryController | OK |
| POST | /library/{trackId} | Library | LibraryController | OK |
| GET | /download/{trackId} | Library | DownloadController | OK |
| GET | /download/{trackId}/signed | Library | DownloadController | OK |
| POST | /payments/checkout | Payments | PaymentsController | OK |
| GET | /payments/state | Payments | PaymentsController | OK |
| GET | /payments/result | Payments | PaymentsController | OK |
| POST | /payments/process | Payments | PaymentsController | **REQ-06** |
| POST | /payouts/connect-stripe | Payouts | PayoutController | OK |
| GET | /payouts/connect-status | Payouts | PayoutController | OK |
| GET | /payouts/stripe-dashboard | Payouts | PayoutController | OK |
| GET | /payouts/account | Payouts | PayoutController | OK |
| POST | /payouts/connect | Payouts | PayoutController | **REQ-07** |
| DELETE | /payouts/disconnect | Payouts | PayoutController | OK |
| POST | /payouts/disconnect | Payouts | PayoutController | OK |
| GET | /payouts/earnings | Payouts | PayoutController | OK |
| POST | /payouts/request | Payouts | PayoutController | OK |
| GET | /payouts/history | Payouts | PayoutController | OK |
| POST | /payouts/settings | Payouts | PayoutController | OK |
| PUT | /payouts/settings | Payouts | PayoutController | OK |
| POST | /purchases | Purchases | PaymentsController | **REQ-04** |
| POST | /purchases/credit-creator | Purchases | PaymentsController | **REQ-05** |
| GET | /settings/profile | Settings | AuthController | OK |
| POST | /settings/password | Settings | AuthController | **VAL-02** |
| PUT | /settings/password | Settings | AuthController | **VAL-02** |
| POST | /settings/email | Settings | AuthController | **VAL-02** |
| PUT | /settings/email | Settings | AuthController | **VAL-02** |
| GET | /stream | Stream | StreamController | OK |
| GET | /stream/{trackId} | Stream | StreamController | OK |
| POST | /stream/start | Stream | StreamController | OK |
| POST | /stream/stop | Stream | StreamController | **RT-03** |
| GET | /subscriptions/plans | Subscriptions | SubscriptionsController | OK |
| GET | /subscriptions/current | Subscriptions | SubscriptionsController | OK |
| POST | /subscriptions/update | Subscriptions | SubscriptionsController | OK |
| POST | /subscriptions/cancel | Subscriptions | SubscriptionsController | OK |
| GET | /subscriptions/history | Subscriptions | SubscriptionsController | OK |
| GET | /tracks | Tracks | CatalogController | OK |
| GET | /tracks/{id} | Tracks | CatalogController | **RT-02** |
| POST | /tracks/upload | Tracks | CatalogController | **REQ-08** |
| GET | /wallet | Wallet | WalletController | OK |
| POST | /wallet/withdraw | Wallet | WalletController | OK |
| GET | /wallet/history | Wallet | WalletController | **QP-02** |
| POST | /webhook/stripe | Webhook | WebhookController | OK |
| GET | /licenses/{trackId} | Licenses | LicensesController | **RT-01** |

---

## Section 10: Recommended Corrections (Priority Order)

### P0 — Critical (breaks client integration)

1. **ENV-01:** Fix Auth endpoints (`/auth/register`, `/auth/login`, `/auth/me`) to use the `ApiResponse` envelope. In `AuthController.cs`, replace `Ok(ToSession(result))` and `StatusCode(201, ToSession(result))` with `OkResponse(ToSession(result))` and `CreatedResponse(ToSession(result))`.

2. **ENV-02:** Update `contracts/openapi.v1.json` to define a generic `ApiResponse<T>` envelope wrapper for all non-Auth endpoint responses (matching the actual backend behavior). This is the single highest-impact fix.

3. **RT-01:** Fix `LicensesController.GetLicense()` route from `{licenseId}` to `{trackId}` and update the service lookup to retrieve by track ID for the current user.

### P1 — Major (schema mismatch, could break serialization)

4. **REQ-05:** Rename `CreditCreatorRequest.AmountCents` to `Amount` and change type to `double` (or update contract).

5. **REQ-04:** Add `Amount` property to `PurchaseCreateRequest`. Decide on `StripeSessionId` (add to contract or remove from DTO).

6. **REQ-07:** Replace inline `PayoutConnectRequest` with `ConnectBankRequest` matching the contract.

7. **RES-01:** Fix `/checkout` response to include `licenseCertificate` field per contract.

8. **RT-03:** Change `/stream/stop` to read `streamId` from request body instead of query parameter.

### P2 — Moderate (validation gaps, missing fields)

9. **REQ-01:** Add `Plan` and `PhoneNumber` to `RegisterRequest`.

10. **REQ-02:** Add password regex pattern to `RegisterRequest.Password`.

11. **REQ-08:** Add `UploadTrackPricingRequest` parameter to `CatalogController.TracksUpload()`.

12. **QP-01/QP-02:** Fix query parameter names for `/community` and `/wallet/history`.

### P3 — Low (cleanup, documentation)

13. **RT-02:** Remove duplicate `/tracks/{id}` from contract.
14. **REQ-06:** Remove deprecated card fields from contract or add as `[Obsolete]` to DTO.
15. **RES-02:** Add `CoverArt` to contract upload schema.
16. **MAN-01/02/03:** Regenerate `endpoint-manifest.v1.json` to include all contract endpoints.
17. **UE-01 through UE-04:** Add undocumented endpoints to contract or remove from backend.
18. **VAL-01/02/03:** Align validation strictness between contract and backend.

---

*End of Compliance Audit Report*
