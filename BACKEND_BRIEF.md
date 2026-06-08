# Cambrian Backend Brief — for the new frontend

Audience: the team rebuilding the Cambrian frontend for the new direction (a creator
**legitimacy / ownership / compliance** platform). This documents the backend **as it
actually exists today** (read from source on 2026-06-04), not as aspired. Where something
does not exist, it says so explicitly — build around what's here, and see §9 for what the
backend still owes you.

Stack: .NET 8 / ASP.NET Core, PostgreSQL (EF Core), Stripe (Connect + Billing), S3-compatible
object storage. No Redis. No background-job framework. No realtime channel.

---

## 0. Conventions you must internalize first

- **JSON casing: camelCase.** No global `AddJsonOptions` override exists, so ASP.NET Core's
  default applies (`Program.cs`). Property `UserId` → `"userId"`, `CambrianTrackId` →
  `"cambrianTrackId"`.
- **Enums serialize as INTEGERS.** There is **no** `JsonStringEnumConverter` registered.
  Any DTO field whose C# type is an enum comes over the wire as a number. The clearest case
  is `EntitlementDto` → `resourceType`, `accessLevel`, `sourceType` are **ints** (see §3).
  ⚠️ However most "enum-like" fields you'll touch (`tier`, `role`, `status`, `visibility`,
  track `status`, `creatorTier` on `/auth/me`) are actually **`string` properties** in the
  DTOs, so they arrive as strings. Treat each field by its documented type below, not by
  assuming.
- **Dates: ISO 8601 UTC** (e.g. `"2026-06-04T12:30:00Z"`), `DateTime`/`DateTime?`.
- **Money: integer cents** in `*Cents` fields (`nonExclusivePriceCents`, `walletBalanceCents`,
  `priceCents`). Some legacy/derived fields are `decimal` dollars (`price`, `nonExclusivePrice`).
  Fee rates are decimal fractions (`0.35` = 35%).
- **IDs:** users are **string** (ASP.NET Identity GUID-as-string). Tracks, Creators,
  Subscriptions, Entitlements are **GUID**. Tracks also carry a human ID
  `cambrianTrackId` = `CAMB-TRK-XXXXXXXX`. Creators are also addressable by **username** slug.
- **Response envelope (almost everything):**
  ```json
  { "success": true,  "data": <payload>, "message": "optional" }
  { "success": false, "error": "human message" }
  ```
  Source: `src/Cambrian.Api/Common/ApiResponse.cs`, `Controllers/BaseController.cs`.
  A few endpoints return bespoke flat objects (Google status, the 402 upgrade body) — noted inline.

---

## 1. Auth & session

### Mechanism — dual-transport JWT ("SmartScheme")
`Program.cs`. Every request is authenticated by a JWT carried **either** way:
- **`Authorization: Bearer <jwt>`** header (API/SPA-with-token), **or**
- **HttpOnly cookie `auth_token`** (set automatically on login/register/google).

The server picks the handler per request: if an `Authorization` header is present it validates
that; otherwise it reads `auth_token`. So a browser SPA can rely purely on the cookie.

- Cookie: `HttpOnly=true`, `SameSite=Lax`, `Secure` in prod, **7-day** expiry, non-sliding.
- JWT lifetime: **120 minutes** (`Jwt:ExpirationMinutes`), issuer `cambrian-api`, audience
  `cambrian-client`, 30s clock skew.
- Also supported: **`X-API-Key`** header (hashed lookup) for programmatic access — `ApiKeyMiddleware`.

### "Refresh"
There is **no OAuth-style refresh-token grant.** Instead `POST /auth/refresh` (auth required)
re-issues a fresh 120-min JWT, and `GET /auth/me` also returns a freshly-minted token each call.
Frontend pattern: call `/auth/me` on load to get user + a fresh token; re-login when the token
finally expires.

### Endpoints (`AuthController`, route prefix `/auth`; settings under `/settings`)
| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/auth/register` | none (rate-limited) | create account, sets cookie |
| POST | `/auth/login` | none (rate-limited) | email+password, sets cookie |
| POST | `/auth/google` | none | Google ID-token login |
| GET | `/auth/google/status` | none | is Google configured |
| GET | `/auth/me` | yes | **current user (rich)** + fresh token |
| POST | `/auth/logout` | yes | clears `auth_token` cookie |
| POST | `/auth/refresh` | yes | new JWT |
| POST | `/auth/set-username` | yes | creator onboarding (promotes to Creator role) |
| GET | `/auth/username-availability` | none | check a username |
| POST | `/auth/set-password` | yes | add password to Google-only account |
| POST | `/auth/link-google` | yes | link Google to local account |
| POST | `/auth/forgot-password` `/auth/verify-code` `/auth/reset-password` | none | 6-digit code reset |
| POST | `/auth/recover-username` | none | email/SMS username |
| GET | `/auth/verify-email` / `/auth/verify-email-change` | none | email verification links |
| POST | `/auth/send-verification-email` | yes | resend verification |
| GET | `/auth/csrf-token` | none | **no-op** (JWT needs no CSRF token) |
| GET/POST/PUT | `/settings/profile` `/settings/password` `/settings/email` | yes | account settings |
| PUT | `/auth/display-name` | yes | rename |

### Login/register/google response (the **session** object) — verified
Returned by `/auth/login`, `/auth/register`, `/auth/google` (`AuthController.ToSession`):
```json
{
  "success": true,
  "data": {
    "token": "eyJhbGciOiJI...",
    "tier": "free",
    "role": "User",
    "isNewUser": true,
    "needsUsername": true,
    "requiresUsernameSetup": true,
    "user": {
      "id": "5f3c...-guid",
      "email": "user@example.com",
      "username": null,
      "displayName": "Jane",
      "phoneNumber": null
    },
    "capabilities": ["license.purchase"]
  }
}
```
(A `Set-Cookie: auth_token=...` accompanies it.) Note: the session object is **leaner** than
`/auth/me` — it does **not** include creatorTier/uploadCount/subscription.

### Current user (rich) — `GET /auth/me` — verified
```json
{
  "success": true,
  "data": {
    "token": "eyJ...freshly-reissued",
    "id": "5f3c...-guid",
    "email": "user@example.com",
    "tier": "pro",
    "role": "Creator",
    "username": "jane-makes-beats",
    "displayName": "Jane",
    "phoneNumber": "+15555550123",
    "isNewUser": false,
    "needsUsername": false,
    "requiresUsernameSetup": false,
    "canChangeUsername": false,
    "creatorTier": "Pro",
    "uploadCount": 12,
    "uploadLimit": null,
    "subscriptionStatus": "Active",
    "subscriptionEndDate": "2026-07-04T00:00:00Z",
    "platformFeePercent": 0.10,
    "contractVersion": "2.1.0",
    "hasPassword": true,
    "googleLinked": false,
    "capabilities": ["license.purchase","track.upload","track.edit.own","track.delete.own","creator.dashboard.view"]
  }
}
```
⚠️ Casing inconsistency to handle: here `tier` is lowercase (`"pro"`) but `creatorTier` is the
capitalized enum/display name (`"Pro"`). `uploadLimit` is `null` for unlimited tiers.

### Roles, "creator", capabilities
- `role` is a **string**: `"User"` | `"Creator"` | `"Admin"` (`ApplicationUser.Role`). A user
  becomes a **Creator** by setting a username (`/auth/set-username`).
- Tier strings: `"free"` | `"creator"` | `"pro"` (also legacy `"paid"`).
- **Capabilities** (`Capabilities.cs` + `CapabilityResolver.cs`) are the granular gate the UI
  should respect. Strings: `license.purchase`, `track.upload`, `track.edit.own`,
  `track.delete.own`, `creator.dashboard.view`, `payout.request`, `invoice.download`,
  `track.license.exclusive`, `track.license.buyout`, `admin.access`. Resolution:
  - everyone → `license.purchase`
  - Creator or Admin → upload/edit/delete-own/dashboard
  - Pro tier or Admin → payout/invoice/exclusive/buyout
  - Admin → `admin.access`
  Capabilities arrive in the login/me payload — gate UI off that array.

### CORS (`StartupExtensions.cs`)
- **Credentials are allowed** (`AllowCredentials()`) so the `auth_token` cookie works cross-origin.
- Allowed origins are an explicit set (localhost dev ports; `cambrianmusic.com` / `www`; staging
  hosts) **plus** dynamic Vercel/Cloudflare preview matching.
- To add a new frontend origin, set config: `App:FrontendUrl`, `App:CorsOrigins`
  (comma-separated), and for previews `App:VercelProjectSlug` / `App:CloudflarePagesSlug`.
- **CSRF:** none beyond `SameSite=Lax` (acceptable because auth is a self-contained JWT, not a
  server session). `/auth/csrf-token` is a compatibility no-op.

---

## 2. API surface

- **OpenAPI:** checked-in canonical spec at `contracts/openapi.v1.json` (~190 paths). Swagger JSON
  at `/swagger/v1/swagger.json`; Swagger UI at `/swagger/ui` (Development only); alias
  `GET /openapi.json`; a derived `GET /manifest.json` lists endpoints + auth.
- **Base URL / versioning:** no global prefix. Most routes are unprefixed (`/auth`, `/catalog`,
  `/billing`, `/upload`). **Public, API-key'd, rate-limited** endpoints live under `/api/v1/*`.
  A handful of "frontend-canonical" routes use `/api/*` (`/api/me/entitlements`,
  `/api/billing/checkout`, `/api/billing/portal`, `/api/tracks`, `/api/stripe/webhook`,
  `/api/entitlements/*`, `/api/creators/*`).

### Error / validation format (`ExceptionMiddleware.cs`)
| Status | Trigger | Body |
|---|---|---|
| 400 | `ArgumentException`/`InvalidOperationException`, model validation | `{ "success": false, "error": "msg" }` |
| 401 | unauth / `UnauthorizedAccessException` | `{ "success": false, "error": "Authentication is required." }` |
| 402 | `UpgradeRequiredException` (plan gate) | `{ "success": false, "error": "...", "code": "UPGRADE_REQUIRED" }` |
| 403 | `ForbiddenException` | `{ "success": false, "error": "Access denied." }` |
| 404 | `KeyNotFoundException` | `{ "success": false, "error": "... not found." }` |
| 500 | anything else | generic message in prod |

- **Model validation (400)** joins all field errors into one string with ` | `:
  `{ "success": false, "error": "Title is required. | Audio file is required." }`
  (custom `InvalidModelStateResponseFactory`, **not** RFC ProblemDetails).
- **402 is the only response with a machine `code`** today (`UPGRADE_REQUIRED`). Detect it to
  launch the upgrade flow. (`USERNAME_REQUIRED` exists as a forbidden-reason string in the
  username filter but is not a standardized code field — treat 403 + message.)

### Pagination / filtering / sorting
List endpoints (e.g. `CatalogController` `/catalog`, `/discover`, `/trending`;
`AiDiscoveryController`; `/api/v1/tracks/search`) use query params:
`page` (1-based), `pageSize` or `limit` (clamped, ~1–100), plus filters `genre`, `mood`,
`tempo`, `instrumental`, `search`, `sort`, `useCase`. Paginated payloads include
`page`, `pageSize`, `totalCount`, `totalPages`, `hasNextPage`, and a previous-page flag.
⚠️ Confirm the exact list-envelope field names against a live response before binding — they
were read from controllers, not a captured response.

### Master endpoint list (by controller)
Auth/account → see §1. The rest (route prefix → notable actions; **A**=anonymous, **Au**=authorize):

- **CatalogController** (`/`): `GET /discover`, `/catalog`, `/trending`, `/tracks`,
  `/tracks/trending`, `/tracks/{id}`, `/track/{id}`, `/catalog/{id}` — **A**, visibility-checked.
- **AiDiscoveryController** (`/ai-discovery`) **A**: `/tracks/search`, `/tracks/{id}`,
  `/tracks/{id}/preview`, `/creators/{id}`. (Machine/agent discovery surface; MCP-related.)
- **UploadController** (`/`): `POST /upload`, `POST /api/tracks` (creator-gated, 150 MB),
  `POST /uploads/image` (**Au**, 10 MB).
- **StreamController** (`/stream`): `GET /stream` (**Au**), `GET /stream/{id}` (**Au**, signed URL),
  `GET /stream/{id}/audio` (**A**, Range-proxy).
- **DownloadController** (`/download`): `GET /download/{id}`, `/{id}/file`, `/{id}/signed`
  (**Au**, gated by purchase entitlement).
- **CreatorController** (`/creator`, creator-gated): `GET /creator/tracks`, `/creator/revenue`,
  `PUT/DELETE /creator/tracks/{id}`, `PUT /creator/tracks/{id}/cover-art`.
- **CreatorsController** (`/api/creators`): `GET /dashboard` (**Au** Creator/Admin),
  `/{guid}`, `/by-username/{username}`, `/{guid}/tracks`, `/resolve/{identifier}` (**A**).
- **CreatorProfileController** (`/creator-profile`): `GET /{slug}`, `/{slug}/storefront`, + profile/
  collection management.
- **UsersController** (`/users`): `GET /users/{username}` (**A** public profile),
  `PATCH /users/me` (**Au** profile edit).
- **BillingController** (`/billing`, **Au**): `POST /billing/checkout`, `/checkout-session`,
  `GET /billing/status`, `GET /billing/checkout-session/{id}`, plus `/api/billing/checkout`,
  `/api/billing/portal`. (See §7.)
- **SubscriptionsController** (`/subscriptions`): `GET /plans` (**A**), `/current`, `POST /update`,
  `/cancel`, `GET /history` (**Au**).
- **MeController** (`/api/me`, **Au**): `GET /api/me/entitlements` (plan feature matrix — §7).
- **EntitlementsController** (`/api/entitlements`): `GET /me`, `GET /access` (**Au**),
  `POST /grant`, `DELETE /{id}` (Admin) — per-resource grants (§7/§8).
- **WebhookController** (`/webhook`, **A** + signature): `POST /webhook/stripe`,
  `POST /api/stripe/webhook`, `POST /webhook/email`.
- **WalletController** (`/wallet`, **Au**): `GET /wallet`, `POST /wallet/withdraw`, `GET /wallet/history`.
- **PayoutController** (`/payouts`, creator + Stripe-Connect gated): `connect-stripe`,
  `connect-status`, `stripe-dashboard`, `account`, `disconnect`, `earnings`, `request`, `history`.
- **InvoiceController** (`/invoices`, **Au**): list, get, `GET /{id}/download`.
- **LibraryController** (`/library`, **Au**): list/add/remove saved tracks; `/purchased-track-ids`.
- **ApiKeysController** (`/api/v1/keys`, **Au**): create (returns raw key once), list, revoke.
- **TracksV1Controller / CreatorsV1Controller** (`/api/v1`, API-key, rate-limited): `tracks/search`,
  `tracks/{id}`, `tracks`, `genres`, `creators/{identifier}`.
- **AnalyticsController** (`/analytics`): `POST /track` (**Au**), `POST /events` (**A**, 202),
  admin summaries.
- **ActivityController** (`/activity`, **A**): `new`, `sales`, `trending`.
- **BoostsController** (`/tracks/{id}/boost`): `POST`/`DELETE` (**Au**, verified-email),
  `GET` (**A**). **CommunityController** (`/community`, **A**): `community`, `hot-this-week`.
- **TierController** (`/tiers`): `GET /tiers/config` (**A**, the tier manifest).
- **FeatureFlagsController** (`/feature-flags`): `GET /check/{name}` (**Au**), admin CRUD.
- **ImageProxyController** (`/images/{**key}`, **A**, cached): image proxy.
- **HealthController** (`/health`), **QaPreflightController** (`/qa-preflight`), **AdminController**
  (`/admin`, Admin), **DebugController** (`/debug`, Admin), **DataController** (`/data`),
  **AiController** (`/ai`).

---

## 3. Data shapes & enums

### User (as returned) — strings, see `/auth/me` in §1
Underlying `ApplicationUser` (`src/Cambrian.Domain/Entities/ApplicationUser.cs`): `Id` (string),
`Email`, `DisplayName?`, `Role` (string), `Tier` (string), `CreatorTier` (enum), `VerifiedCreator`
(bool), `UploadCount` (int), `SubscriptionStatus` (string), `SubscriptionEndDate?`,
`StripeAccountId?` (Connect), `WalletBalanceCents` (long), `ProfileImageUrl?`, `CoverImageUrl?`,
`Bio?` (≤500), `GoogleId?`, `AuthProvider?`, `EmailVerified` (bool).

### Creator / CreatorProfile
- `Creator` (`Entities/Creator.cs`): `Id` (GUID), `UserId` (string), `Username` (slug, unique),
  `DisplayName?`, `Bio`, `ProfileImageUrl?`, `CoverImageUrl?`, `SocialLinks?` (JSON array of
  `{platform,url}`), `CreatedAt`, `UpdatedAt`.
- `CreatorProfile` (storefront): `Slug`, `BannerImageUrl?`, `Bio`, `Niche?`, `SocialLinks?`,
  `ShowEarnings`, `ShowDownloadStats`, `PinnedTrackIds?` (comma-sep).
- Public shape `PublicCreatorDto` adds a `stats` block (`trackCount`, `totalSales`,
  `totalDownloads`, `averageRating`, `followerCount`) and a `tracks` array.

### Track (`TrackResponse`, `DTOs/Catalog/TrackResponse.cs`)
```json
{
  "id": "guid",
  "cambrianTrackId": "CAMB-TRK-A1B2C3D4",
  "title": "Midnight Dreams",
  "description": "…",
  "genre": "electronic", "primaryGenre": "Electronic", "subgenre": "Synthwave",
  "mood": "chill", "tempo": "90", "instrumental": true,
  "visibility": "public",
  "price": 9.99,
  "nonExclusivePrice": 9.99, "exclusivePrice": 49.99, "copyrightBuyoutPrice": 199.99,
  "platformFeePercent": 0.15,
  "nonExclusiveCreatorEarnings": 8.49,            // plus *PlatformFee / *CreatorEarnings per tier
  "exclusiveSold": false,
  "status": "available",
  "isCopyrightTransferred": false,
  "duration": "3:45",
  "audioUrl": "https://api.../stream/{id}/audio",
  "coverArtUrl": "https://api.../images/covers/...",
  "creatorId": "guid", "creatorSlug": "jane-makes-beats",
  "creatorProfileImageUrl": "https://…", "artist": "Jane",
  "createdAt": "2026-05-01T10:00:00Z"
}
```
⚠️ The price/license tier fields (`*Price`, `exclusiveSold`, `status` transitions, copyright
buyout) are **legacy marketplace** surface — see §8.

### Subscription (`SubscriptionResponse`)
`{ "id": guid, "plan": "free|creator|pro|paid", "status": "active|cancelled|expired",
"startedAt": iso, "expiresAt": iso|null }`.

### Enums (`src/Cambrian.Domain/Enums/`) — remember: serialize as **ints** when the DTO field is the enum type
- `CreatorTier`: `Free=0`, `Pro=1`, `Creator=2` (append-only; **do not assume 0/1/2 = cheap→expensive**).
- `EntitlementAccessLevel`: `Stream=1`, `Download=2`, `License=3`, `Admin=4` (ranked; higher satisfies lower).
- `EntitlementResourceType`: `Track=0`, `Collection=1`, `CreatorSubscription=2`, `ExclusiveContent=3`.
- `EntitlementSourceType`: `Purchase=0`, `Subscription=1`, `Tip=2`, `Promotion=3`, `Admin=4`.
- String "enums" (arrive as strings): track `status` (`available`/`exclusive_sold`/
  `copyright_transferred`), `visibility` (`public`/`limited`/`hidden`), `role`, `tier`,
  `subscriptionStatus` (`Active`/`Inactive`/`Cancelled`/`PastDue`).

`EntitlementDto` example (note integer enums):
```json
{ "id":"guid","userId":"u_1","resourceType":0,"resourceId":"guid","accessLevel":2,
  "sourceType":0,"sourceId":"purchase_123","grantedAt":"…Z","expiresAt":null,
  "revokedAt":null,"revokedReason":null }
```

---

## 4. Media / file handling

- **Upload:** `POST /upload` or `POST /api/tracks`, `multipart/form-data`, `[FromForm]
  UploadTrackRequest` (`Audio` file required, optional `CoverArt`, `Title`, genres, prices…).
  **150 MB** request limit. (No presigned-PUT direct-to-bucket upload path is wired for the
  client — uploads go through the API. `S3ObjectStorage` *can* mint upload URLs but no endpoint
  exposes them.)
- **Audio formats:** `.mp3 .wav .flac .aac .ogg .m4a`, validated by extension **and MIME and
  magic bytes** (`UploadService.cs`). Stored at `tracks/{creatorId}/{guid}{ext}`.
- **Cover art:** `.jpg .jpeg .png .webp`, **10 MB**, stored `covers/{creatorId}/{guid}{ext}`.
- **Serving audio:**
  - `GET /stream/{id}` → returns a (signed, ~15-min) URL, **auth required**.
  - `GET /stream/{id}/audio` → backend **Range-proxy** (HTTP 206, Safari/iOS-safe), anonymous but
    visibility-checked. This is the CORS-safe player source.
  - `GET /download/{id}` → presigned download URL + filename, **15-min expiry**, gated by a
    **purchase entitlement** (`IEntitlementService.CanDownloadAsync`). `/{id}/file` streams bytes
    as a fallback (local storage).
- **Images:** served through `GET /images/{**key}` proxy (avoids R2/S3 CORS); 24h cache.
- **Storage providers:** `LocalObjectStorage` (dev, `wwwroot/uploads`, public `/uploads/...`),
  `S3ObjectStorage` (staging/prod, presigned URLs via SigV4; talks to a Supabase S3 gateway with
  path-style). `R2ObjectStorage` is a **stub** (unimplemented).

---

## 5. Business rules the UI must respect

- **Track limit (per tier):** Free = **10** tracks; Creator/Pro = unlimited
  (`TierManifest`, enforced in `UploadService`). Exceeding it returns **402 `UPGRADE_REQUIRED`**.
- **Gating stack on creator write endpoints:** `VerifiedEmail` policy (email must be verified) +
  `RequireCreatorTier` (role Creator/Admin) + `RequireUsername` (username set) +
  capability policy (`CanUploadTrack`, `CanEditOwnTrack`, `CanDeleteOwnTrack`). Payouts add
  `RequireStripeConnectEnabled`. The UI should pre-check these (verified email, username set,
  capability present) and surface the right onboarding step.
- **Rate limits** (HTTP 429): global ~100/min per IP (500 in dev/staging), `auth` policy ~10/min
  (login/register/reset), `/api/v1/*` ~100/min per API key, `community` (boosts) ~30/min.
- **Track lifecycle** is **string status, no enforced state machine**: `available` →
  `exclusive_sold` (after exclusive license sale) → `copyright_transferred` (after buyout, sets
  `copyrightOwnerId`/`copyrightTransferredAt`, preserves `originalCreatorId`). These transitions
  are marketplace-era (§8); for the new model treat `status` as informational.
- **Visibility:** `public` (discoverable) / `limited` (link-only) / `hidden` (private).

---

## 6. Async / long-running work

**There is essentially none today, and no notification channel.** Verified absent:
- **No on-chain / provenance anchoring.** `provenanceStamp` / `fullProvenanceSuite` are
  advertised feature flags in `TierManifest` but have **no implementation** (no `IProvenanceAnchor`,
  no chain client, no hashing-on-upload).
- **No PDF/certificate generation.** The `LicenseCertificates` table was **dropped**
  (migration `20260601023428_DropLicenseCertificates`). A `CertificatePdfTheme.cs` artifact
  remains but is unreferenced; QuestPDF is not used.
- **No DDEX / C2PA** export.
- **No background-job framework** (no Hangfire/Quartz/`IHostedService` worker queue). Uploads are
  fully synchronous.
- **No SSE, no WebSocket, no status-polling endpoint.** (`grep` for `text/event-stream`/`/sse`
  finds nothing live.) When async provenance/cert work is built (§9), a status-polling endpoint
  or push channel will have to be added too.

---

## 7. Stripe / billing & entitlements

### What exists
- **Stripe Connect (creator payouts) — mature.** Express accounts, onboarding links, dashboard
  links, transfers, disconnect (`StripeFacade`, `PayoutController`, `WalletController`). Creator's
  Connect account id is `ApplicationUser.StripeAccountId`. Earnings accrue to `walletBalanceCents`.
- **Stripe Billing (subscriptions) — present (Phase 1).** Tiers Creator/Pro map to **pre-created
  Stripe Price IDs** from config (`Stripe:Prices:Creator`, `Stripe:Prices:Pro`).
  - `POST /api/billing/checkout` `{ "tier": "creator"|"pro" }` → `{ "checkoutUrl": "…" }`.
  - `POST /api/billing/portal` → `{ "portalUrl": "…" }` (self-service manage/cancel; needs the
    Stripe Customer Portal enabled in the dashboard).
  - `GET /billing/status` and `GET /billing/checkout-session/{id}` (post-redirect confirm).
  - Webhook `POST /api/stripe/webhook` (signature-verified, idempotent) handles
    `checkout.session.completed`, `customer.subscription.updated/deleted`,
    `invoice.paid/payment_failed`, plus refund/dispute.
- **Plan entitlements (feature matrix):** `GET /api/me/entitlements` — **this is what the new UI
  should gate features on.**
  ```json
  { "success": true, "data": {
    "plan": "creator",
    "status": "active",
    "limits": { "maxTracks": null },
    "features": {
      "provenanceStamp": true, "complianceScoreRead": true,
      "unlimitedTracks": true, "fullProvenanceSuite": true, "pdfCertificates": true,
      "commercialRightsVerification": true, "verifiedCleanBadge": true, "ddexC2pa": true,
      "routingGuidance": true, "catalogAnalytics": true,
      "copyrightOfficeAssist": false, "bulkUpload": false, "syncPool": false,
      "apiAccess": false, "prioritySupport": false
    } } }
  ```
  ⚠️ These feature **flags are real and tier-correct, but most of the underlying features are not
  built yet** (provenance, certs, DDEX/C2PA, routing, sync pool — see §6/§9). The flag tells you
  what the plan is *entitled to*, not that the capability exists server-side.
- **Per-resource entitlements** (`/api/entitlements/me`, `/access`, admin `grant`/`{id}`) are a
  separate, generic access-control grant table (who can stream/download a specific track). Distinct
  from the plan matrix above. Most relevant today for download gating.

### Tier manifest (`TierManifest.cs`, also `GET /tiers/config`)
| Plan | slug | price | maxTracks | platform fee |
|---|---|---|---|---|
| Free | `free` | $0 | 10 | 35% |
| Creator | `creator` | $15/mo (1500¢) | unlimited | 15% |
| Pro / Label | `pro` | $39/mo (3900¢) | unlimited | 10% |

### Publishable config for the frontend
- **No Stripe publishable key is exposed by the API**, and **none is needed** — checkout and portal
  use **redirect** (server returns a Stripe-hosted `checkoutUrl`/`portalUrl`; you `window.location`
  to it). No Stripe.js/Elements is wired. **No secret keys are ever sent client-side** (verified).
- If you later add Stripe Elements you'll need a publishable key surfaced via config — not present today.

---

## 8. Legacy (old licensing marketplace) vs. current

**Deprecating / marketplace-era — don't build the new UI around these:**
- **Deleted controllers (gone from `src`):** `CheckoutController`, `PaymentsController`,
  `LicensesController`, `v1/LicensesV1Controller`. The buyer→license-purchase→checkout flow **no
  longer has endpoints.**
- **Dropped table:** `LicenseCertificates` (PDF license certs).
- **Vestigial but still present:** `Purchase` table/`Purchases` (historical only; `LicenseId` is
  inert), track **license-tier pricing** (`nonExclusivePriceCents`/`exclusivePriceCents`/
  `copyrightBuyoutPriceCents`, `exclusiveSold`, `status` buyout transitions), `InvoiceController`,
  `WalletController` + `PayoutController` (creator earnings/payouts — these *may* carry forward if
  the new model still pays creators, but they're rooted in the sale flow). `LibraryController`
  ("purchased tracks"), buyer-side `license.purchase` capability and `track.license.*`.
- **Browse/discovery** (`/catalog`, `/discover`, `/trending`, `AiDiscoveryController`) is
  marketplace-shaped but the **track/creator read models carry forward** — reuse them for catalog/
  profile views even if you drop the buy buttons.

**Carries forward into the new model:**
- All of **auth/account/onboarding** (§1), **creator identity & profiles** (Creator/CreatorProfile,
  `/creator-profile`, `/users/{username}`), **track upload + media serving** (§4), **subscription
  billing + plan entitlements** (§7), **feature flags**, **analytics events**, **API keys / public
  v1 API**, **admin**.

---

## 9. Gaps for the new direction (sequence backend before/with frontend)

The new positioning (provenance, verification, compliance metadata) is **almost entirely
unbuilt** on the backend. Concretely missing:
1. **Provenance anchoring** — content-hash-on-upload + pluggable on-chain anchor + a
   `GET /tracks/{id}/provenance` read. (Flag `provenanceStamp` exists; implementation doesn't.)
2. **Compliance score** — no compute/store/read (`complianceScoreRead` flag only).
3. **Authorship documentation** — no model/endpoints (edits/arrangement/lyrics/process notes).
4. **Provenance certificate (PDF)** — generation removed; needs rebuild + async job + delivery.
5. **Commercial-rights verification / "Verified Clean" badge** — no state or endpoint.
6. **DDEX AI-disclosure + C2PA embedding** — none.
7. **Distribution routing guidance, sync-pool, Copyright-Office assist, bulk upload** — none.
8. **Async infrastructure** — there is no job queue and **no completion channel** (SSE/WS/poll).
   Anything long-running (anchoring, cert gen, distribution) needs both a worker and a status
   endpoint the UI can poll. Plan this jointly.
9. **A consolidated "track detail for creators"** view (provenance + compliance + authorship +
   badge) does not exist; today's `TrackResponse` is sale-oriented.

Recommended sequencing: backend lands content-hash + provenance read + compliance-score read +
authorship model first (these unblock the core "legitimacy" UI), with a status-polling endpoint
for cert/anchor jobs; certificates/DDEX/C2PA/routing follow.

---

## 10. Environments

`render.yaml` + `appsettings.*`:
- **Development:** `dotnet run`, `http://localhost:5000` (frontend dev origins `:5173/:5174/:4174`);
  storage `local`; email `console`; rate limits relaxed (500/100).
- **Staging:** Render service from branch **`staging`**; env `Staging`; S3 bucket
  `cambrian-audio-staging`; email via **Resend**; CORS includes the Vercel preview + staging hosts;
  `Checkout:RequireSubscription=true`.
- **Production:** Render service from branch **`main`**; env `Production`; S3 bucket
  `cambrian-audio-prod`; Resend; CORS `cambrianmusic.com`/`www`; rate limits 100/10; storage must
  be S3.
- **Exact public API hostnames** for staging/prod are not hardcoded in source (set via Render env /
  DNS) — confirm the live base URLs with whoever owns Render before wiring the frontend.
- **Feature flags** are DB-driven (`FeatureFlags` table, `GET /feature-flags/check/{name}`); known
  seeded flags include `CheckoutV2Enabled` and `StripeConnectEnabled`. Use these for staged rollout.

---

### Quick "things that will bite you" list
- Enum DTO fields are **numbers** (`accessLevel: 2`), but tier/role/status are **strings**.
- `/auth/login` returns a **leaner** object than `/auth/me`; fetch `/auth/me` for full user state.
- `creatorTier` is `"Pro"` (capitalized) on `/auth/me` but the plan slug is `"pro"` elsewhere.
- `uploadLimit: null` means unlimited, not zero.
- Subscriptions use **redirect** checkout/portal — no Stripe.js, no publishable key.
- The compliance/provenance **feature flags are live but the features are not** — gate UI on
  `/api/me/entitlements` but expect 404 / "not implemented" for the actual provenance/cert/DDEX
  endpoints until §9 ships.
