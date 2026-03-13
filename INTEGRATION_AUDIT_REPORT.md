# Cambrian Platform Integration Audit Report

**Date:** 2026-03-13  
**Auditor:** Integration QA Engineer (Automated)  
**Scope:** Backend API, OpenAPI Contract, Endpoint Manifest  
**Repository:** Cambrian-Backend-Api (backend only; no frontend repo available)

---

## Executive Summary

**Overall Status: FAIL**

Out of 11 integration flows audited, **6 FAIL** and **5 PASS**. The failures are concentrated around **response envelope mismatches** (auth endpoints bypass the standard `ApiResponse` wrapper), **OpenAPI contract drift** (field-level schema mismatches, missing endpoints, incorrect parameter bindings), and a **critical purchase-blocking schema mismatch** where the OpenAPI contract does not include the `StripeSessionId` field required by the backend.

---

## Flow-by-Flow Audit

---

### 1. Register

**TEST:** POST `/auth/register` — user registration with JWT issuance  
**STATUS: FAIL**  
**LAYER WHERE FAILURE OCCURS:** API Controller → OpenAPI Contract  
**ROOT CAUSE:** Response envelope mismatch. The controller returns `StatusCode(201, ToSession(result))` which produces a bare object `{token, tier, user: {id, email, tier, role}}`. It does **not** use `BaseController.CreatedResponse()`, so the response is **not** wrapped in the standard `{success, data, message, error}` envelope. The OpenAPI contract declares the response as `ApiResponseOfAuthSession` which expects the `{success, data: AuthSession}` wrapper. Any generated API client will fail to deserialize the register response.

**Additional findings:**
- OpenAPI `RegisterRequest` schema includes `plan` and `phoneNumber` fields that do **not** exist on the actual `RegisterRequest` DTO. Frontend clients will send these fields; the backend silently ignores them.
- OpenAPI password pattern `^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[\W_]).{8,}$` enforces complexity, but the DTO only validates `[MinLength(8)]`. Backend will accept weaker passwords than the contract implies.

**RISK LEVEL:** HIGH  
**RECOMMENDATION:** Use `CreatedResponse(ToSession(result), "Account created.")` in `AuthController.Register()` so the response is wrapped in the standard API envelope. Add `plan` and `phoneNumber` to the DTO or remove them from the OpenAPI schema.

---

### 2. Login

**TEST:** POST `/auth/login` — user authentication with JWT issuance  
**STATUS: FAIL**  
**LAYER WHERE FAILURE OCCURS:** API Controller → OpenAPI Contract  
**ROOT CAUSE:** Same envelope mismatch as Register. The controller returns `Ok(ToSession(result))` instead of `OkResponse(ToSession(result))`. The response is a bare `{token, tier, user}` object, not wrapped in `{success, data}`. The OpenAPI contract declares `ApiResponseOfAuthSession`.

**Additional finding:** The `GET /auth/me` endpoint has the same problem — it returns `Ok(new { token, user })` bypassing the `OkResponse()` wrapper while the OpenAPI declares `ApiResponseOfAuthSession`.

**RISK LEVEL:** HIGH  
**RECOMMENDATION:** Replace `Ok(ToSession(result))` with `OkResponse(ToSession(result))` in `Login()`. Fix `Me()` similarly.

---

### 3. Upload Track

**TEST:** POST `/upload` — multipart audio file upload by creator  
**STATUS: PASS**  
**LAYER WHERE FAILURE OCCURS:** N/A  
**ROOT CAUSE:** N/A

**Trace:**
- Frontend → POST `/upload` (multipart/form-data, Auth + Creator tier required)
- `UploadController.Upload()` → sets `CreatorId` from JWT claims
- `UploadService.Upload()` → validates file type, size, extension, MIME; uploads to object storage; creates `Track` entity with `CambrianTrackId`
- Persistence → `TrackRepository.AddAsync()`

**Minor note:** The `CoverArt` field on `UploadTrackRequest` is not documented in the OpenAPI schema. This is undocumented but non-breaking since multipart form fields are additive.

**RISK LEVEL:** LOW  
**RECOMMENDATION:** Add `CoverArt` to the OpenAPI `/upload` schema for completeness.

---

### 4. Catalog Listing

**TEST:** GET `/discover`, `/catalog`, `/trending`, `/tracks/{trackId}` — public catalog browsing  
**STATUS: FAIL**  
**LAYER WHERE FAILURE OCCURS:** OpenAPI Contract / Endpoint Manifest  
**ROOT CAUSE:** Auth requirement mismatch. The `CatalogController` does **not** have `[Authorize]` on `Discover()`, `Catalog()`, `Trending()`, or `GetTrack()` — these are public endpoints. However:
1. The OpenAPI spec has global `security: [{"Bearer": []}]` and these paths do **not** override with `security: []`, implying auth is required.
2. The endpoint manifest explicitly lists all catalog endpoints as `requiresAuth: true`.

A generated API client will attach Bearer tokens to these calls and may refuse to call them without a token, even though the backend serves them to anonymous users.

**RISK LEVEL:** MEDIUM  
**RECOMMENDATION:** Add `security: []` overrides to the catalog paths in `openapi.v1.json` and update the endpoint manifest to `requiresAuth: false`.

---

### 5. Streaming Playback

**TEST:** GET `/stream/{trackId}`, GET `/stream/{trackId}/audio`, POST `/stream/start`, POST `/stream/stop`  
**STATUS: FAIL**  
**LAYER WHERE FAILURE OCCURS:** OpenAPI Contract (missing endpoint) + Parameter binding mismatch  
**ROOT CAUSE:** Two issues:

1. **Missing endpoint:** `GET /stream/{trackId}/audio` — the actual audio byte-streaming proxy endpoint that `<audio>` elements target — is completely absent from the OpenAPI spec and endpoint manifest. This is the most important streaming endpoint; catalog responses point `audioUrl` at it.

2. **Parameter binding mismatch on `/stream/stop`:** The OpenAPI spec defines `StreamStopRequest` with `streamId` in the request body, but the controller reads `streamId` from `[FromQuery]`:
   ```csharp
   public async Task<IActionResult> Stop([FromQuery] string? streamId = null)
   ```
   A generated client will send `streamId` in the body; the backend will ignore it and see `null`.

**RISK LEVEL:** HIGH  
**RECOMMENDATION:** Add `GET /stream/{trackId}/audio` to the OpenAPI spec (note: it is `[AllowAnonymous]`). Fix `/stream/stop` to read from body or update the OpenAPI to use a query parameter.

---

### 6. Purchase Track

**TEST:** POST `/purchases` — create a verified purchase record  
**STATUS: FAIL**  
**LAYER WHERE FAILURE OCCURS:** OpenAPI Contract → Backend Service  
**ROOT CAUSE:** Critical schema mismatch between the OpenAPI `PurchaseCreateRequest` and the actual DTO:

| Field | OpenAPI | Actual DTO |
|-------|---------|-----------|
| `amount` | Present (number, double) | **Missing** |
| `stripeSessionId` | **Missing** | Present (string) |

The `PurchaseService.CreateAsync()` method **requires** `StripeSessionId` to verify payment with Stripe before creating the purchase. Without it, the service throws `"A Stripe checkout session ID is required to complete a purchase."`. Any frontend generated from the OpenAPI contract will **never** send `stripeSessionId`, causing all purchase attempts to fail.

**RISK LEVEL:** CRITICAL  
**RECOMMENDATION:** Add `stripeSessionId` to the OpenAPI `PurchaseCreateRequest` schema and remove the `amount` field (or mark it deprecated). This is a blocking integration defect.

---

### 7. Stripe Checkout

**TEST:** POST `/checkout` — create Stripe checkout session; GET `/checkout/session/{sessionId}` — confirm payment  
**STATUS: FAIL**  
**LAYER WHERE FAILURE OCCURS:** OpenAPI Contract  
**ROOT CAUSE:** Multiple schema discrepancies:

1. **`licenseType` nullability:** OpenAPI defines it as nullable with no default. The DTO marks it `[Required]` with a default of `"standard"` and a regex pattern allowing only `standard|non-exclusive|exclusive`. A generated client may omit it; ASP.NET validation will reject the request.

2. **Response shape:** The controller returns `OkResponse(new { checkoutUrl, status })` → `{success: true, data: {checkoutUrl, status}}`. The OpenAPI response schema is `{checkoutUrl, licenseCertificate}` (no `status` field, no `ApiResponse` wrapper). The `licenseCertificate` field is only populated after payment, not at checkout creation time.

3. **`GET /checkout/session/{sessionId}`** has no response schema defined in the OpenAPI — the generated client will have no type information for the confirmation response.

**RISK LEVEL:** HIGH  
**RECOMMENDATION:** Fix the OpenAPI response schema to match `ApiResponse<{checkoutUrl, status}>`. Make `licenseType` required or give it a default. Add a response schema for the session confirmation endpoint.

---

### 8. Webhook Processing

**TEST:** POST `/webhook/stripe` — Stripe webhook event ingestion  
**STATUS: PASS**  
**LAYER WHERE FAILURE OCCURS:** N/A  
**ROOT CAUSE:** N/A

**Trace:**
- Stripe → POST `/webhook/stripe` (no auth, raw body + `Stripe-Signature` header)
- `WebhookController.Stripe()` → reads body/signature, delegates to `IWebhookService`
- `StripeWebhookService.HandleStripeAsync()`:
  - Verified path: validates signature via `EventUtility.ConstructEvent()`
  - Development fallback: parses JSON without signature (dev only)
  - Non-dev without valid signature: rejects with exception
- `ProcessEventAsync()`:
  - Idempotency via `StripeWebhookEvents` ledger table
  - Transactional processing (begin/commit/rollback)
  - `checkout.session.completed` → creates Purchase + LibraryItem + WalletTransaction + LicenseCertificate
  - `customer.subscription.deleted` → logged (manual review needed; StripeCustomerId not stored on user yet)
  - `invoice.payment_failed` → logged

**Findings:** Webhook processing is well-implemented with idempotency, atomic transactions, and proper signature verification in production. The `customer.subscription.deleted` handler cannot automatically downgrade users because `StripeCustomerId` is not stored on `ApplicationUser` — this requires manual review.

**RISK LEVEL:** LOW  
**RECOMMENDATION:** Store `StripeCustomerId` on `ApplicationUser` during checkout to enable automatic subscription downgrade on cancellation.

---

### 9. License Generation

**TEST:** License certificate issuance on purchase + license retrieval via API  
**STATUS: FAIL**  
**LAYER WHERE FAILURE OCCURS:** OpenAPI Contract → Controller routing  
**ROOT CAUSE:** Critical path/parameter mismatch:

- **OpenAPI:** `GET /licenses/{trackId}` with path parameter `trackId` (pattern: `^CAMB-TRK-[A-Z0-9]{4,12}$`) — implies lookup by Cambrian Track ID
- **Controller:** `GET /licenses/{licenseId}` — parameter is `licenseId`, and `GetByIdAsync(licenseId)` does a GUID-based license ID lookup

A frontend client generated from the OpenAPI will call `GET /licenses/CAMB-TRK-A1B2` with a Cambrian track ID. The backend will try `Guid.TryParse("CAMB-TRK-A1B2")`, fail, and return null/404. **Licenses are unreachable via the documented contract.**

Additionally: `GET /licenses` (list all user's licenses) exists in the controller but is **absent** from the OpenAPI spec.

**License issuance itself is correct:** `LicenseService.IssueCertificateAsync()` is called from both `CheckoutService.ConfirmAsync()` and `StripeWebhookService.HandleTrackPurchase()`, creating `LicenseCertificate` entities with proper terms and idempotency.

**RISK LEVEL:** CRITICAL  
**RECOMMENDATION:** Either (a) change the OpenAPI path to `GET /licenses/{licenseId}` with UUID format, or (b) add a new endpoint `GET /licenses/track/{trackId}` that looks up by Cambrian Track ID. Add `GET /licenses` to the OpenAPI spec.

---

### 10. Library Access

**TEST:** GET `/library`, POST `/library`, DELETE `/library/{trackId}`, POST `/library/{trackId}`, GET `/library/purchased-track-ids`  
**STATUS: PASS**  
**LAYER WHERE FAILURE OCCURS:** N/A  
**ROOT CAUSE:** N/A

**Trace:**
- All endpoints match between controller, OpenAPI spec, and endpoint manifest
- `LibraryService` correctly cross-references purchases to mark purchased items
- `GetPurchasedTrackIdsAsync()` returns only completed purchase track IDs
- Duplicate prevention on save
- Proper JWT user ID extraction

**RISK LEVEL:** LOW  
**RECOMMENDATION:** None.

---

### 11. Track Download

**TEST:** GET `/download/{trackId}`, GET `/download/{trackId}/signed` — download purchased tracks  
**STATUS: PASS**  
**LAYER WHERE FAILURE OCCURS:** N/A  
**ROOT CAUSE:** N/A

**Trace:**
- `DownloadController.Download()` → verifies library ownership via `ILibraryRepository.GetByUserAndTrackAsync()` → fetches file from object storage → streams with Content-Disposition
- `DownloadController.SignedUrl()` → same ownership check → generates pre-signed URL (or falls back to direct download path for local storage)
- Proper 403 for non-purchased tracks, 404 for missing audio

**RISK LEVEL:** LOW  
**RECOMMENDATION:** None.

---

## OpenAPI Contract Alignment Summary

| Area | Status | Details |
|------|--------|---------|
| Auth response envelope | FAIL | Register/Login/Me bypass `ApiResponse` wrapper |
| RegisterRequest fields | FAIL | `plan`, `phoneNumber` in OpenAPI but not in DTO |
| Password validation | FAIL | OpenAPI enforces complexity regex; DTO only checks length |
| Catalog auth requirement | FAIL | OpenAPI/manifest say auth required; controller is public |
| Upload CoverArt field | MINOR | Exists in DTO, missing from OpenAPI |
| Stream audio endpoint | FAIL | `GET /stream/{trackId}/audio` missing from contract |
| Stream stop parameter | FAIL | OpenAPI says body; controller reads from query |
| Checkout licenseType | FAIL | OpenAPI says nullable; DTO says required |
| Checkout response shape | FAIL | OpenAPI schema doesn't match actual response |
| PurchaseCreateRequest | FAIL | `stripeSessionId` missing from OpenAPI; `amount` in OpenAPI but not DTO |
| Licenses endpoint path | FAIL | OpenAPI says `{trackId}` (CAMB pattern); controller uses `{licenseId}` (GUID) |
| Licenses list endpoint | FAIL | `GET /licenses` exists in controller, absent from OpenAPI |
| Endpoint manifest drift | FAIL | Missing `/subscriptions/cancel`, `/subscriptions/history` |
| Webhook body schema | OK | Acceptable for Stripe webhooks to lack OpenAPI body schema |
| Library endpoints | PASS | All paths match |
| Download endpoints | PASS | All paths match |

---

## Risk Summary

| Risk Level | Count | Flows |
|------------|-------|-------|
| CRITICAL | 2 | Purchase Track (blocking), License Generation (unreachable) |
| HIGH | 3 | Register, Login, Streaming Playback, Stripe Checkout |
| MEDIUM | 1 | Catalog Listing |
| LOW | 5 | Upload Track, Webhook Processing, Library Access, Track Download |

---

## Top Recommendations (Priority Order)

1. **[CRITICAL] Fix `PurchaseCreateRequest` OpenAPI schema** — add `stripeSessionId`, remove `amount`. Without this, no frontend can complete a purchase.
2. **[CRITICAL] Fix `/licenses/{trackId}` OpenAPI path** — change to `{licenseId}` with UUID format, or implement a track-ID-based lookup. Add `GET /licenses` to the contract.
3. **[HIGH] Wrap auth responses in `ApiResponse` envelope** — use `OkResponse()`/`CreatedResponse()` in Register, Login, and Me endpoints.
4. **[HIGH] Add `GET /stream/{trackId}/audio` to OpenAPI** — this is the primary audio delivery endpoint.
5. **[HIGH] Fix `stream/stop` parameter binding** — align query vs body between controller and contract.
6. **[HIGH] Fix checkout `licenseType` contract** — make it required in OpenAPI or optional in DTO.
7. **[MEDIUM] Fix catalog auth documentation** — add `security: []` overrides for public catalog endpoints.
8. **[LOW] Sync endpoint manifest** — add missing subscription endpoints, fix auth flags.
