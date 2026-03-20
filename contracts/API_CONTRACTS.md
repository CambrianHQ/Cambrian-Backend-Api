# API Contracts

> Canonical source of truth for all API endpoint shapes.
> Any change to request/response shapes MUST be reflected here FIRST.
> Last updated: 2026-03-19 | Contract version: 2.1.0

---

## Conventions

- All prices exposed as **dollars (decimal)** in responses, stored as **cents (int)** in database.
- Dates returned as **ISO 8601 UTC** strings.
- Creator public identity is **`slug`** (from CreatorProfile) or **`displayName`** — NEVER email.
- The `Artist` field in track responses maps to `DisplayName` of the creator.
- Nullable fields omitted or returned as `null`.
- Paginated responses use `PagedResult<T>` wrapper.

---

## Auth

### `POST /auth/register`

**Request:**
```json
{
  "email": "user@example.com",
  "password": "SecurePass123!",
  "displayName": "Studio Nova",
  "role": "Creator"
}
```

**Response (200):**
```json
{
  "userId": "abc-123",
  "email": "user@example.com",
  "token": "eyJ...",
  "tier": "free",
  "role": "Creator"
}
```

### `POST /auth/login`

**Request:**
```json
{
  "email": "user@example.com",
  "password": "SecurePass123!"
}
```

**Response (200):** Same as register (`AuthResponse`).

### `GET /auth/me` (Authorized)

**Response (200): `UserProfileResponse`**
```json
{
  "userId": "abc-123",
  "email": "user@example.com",
  "displayName": "Studio Nova",
  "role": "Creator",
  "tier": "pro",
  "verifiedCreator": true,
  "creatorTier": "Pro",
  "uploadCount": 5,
  "uploadLimit": null,
  "subscriptionStatus": "Active",
  "subscriptionEndDate": "2026-04-19T00:00:00Z",
  "platformFeePercent": 0.15,
  "contractVersion": "1.0.0"
}
```

### `POST /auth/logout` (Authorized)

**Response (200):**
```json
{ "message": "Logged out" }
```

### `GET /auth/health`

**Response (200):**
```json
{ "status": "healthy" }
```

### `GET /auth/csrf-token`

**Response (200):**
```json
{ "csrfToken": "..." }
```

### `POST /auth/forgot-password`

**Request:**
```json
{ "email": "user@example.com" }
```

**Response (200):**
```json
{ "message": "If the account exists, a reset code has been sent." }
```

### `POST /auth/verify-code`

**Request:**
```json
{ "email": "user@example.com", "code": "123456" }
```

**Response (200):**
```json
{ "valid": true }
```

### `POST /auth/reset-password`

**Request:**
```json
{
  "email": "user@example.com",
  "code": "123456",
  "newPassword": "NewSecure456!"
}
```

**Response (200):**
```json
{ "message": "Password reset successful." }
```

### `POST /auth/recover-username`

**Request:**
```json
{ "email": "user@example.com" }
```

**Response (200):**
```json
{ "message": "If the account exists, your username has been sent." }
```

---

## Settings

### `GET /settings/profile` (Authorized)

**Response (200):** Same as `GET /auth/me` (`UserProfileResponse`).

### `POST /settings/password` | `PUT /settings/password` (Authorized)

**Request:**
```json
{
  "currentPassword": "OldPass123!",
  "newPassword": "NewPass456!"
}
```

**Response (200):**
```json
{ "message": "Password updated." }
```

### `POST /settings/email` | `PUT /settings/email` (Authorized)

**Request:**
```json
{
  "password": "CurrentPassword!",
  "newEmail": "newemail@example.com"
}
```

**Response (200):**
```json
{ "message": "Email updated." }
```

---

## Catalog (Public)

### `GET /discover` | `GET /catalog`

**Query Parameters:**
| Param | Type | Default | Description |
|-------|------|---------|-------------|
| page | int | 1 | Page number |
| pageSize | int | 20 | Items per page (max 100) |
| genre | string? | — | Filter by genre |
| search | string? | — | Full-text search on title/artist |
| mood | string? | — | Filter: happy, dark, chill, energetic |
| tempo | string? | — | Filter by BPM/tempo |
| instrumental | bool? | — | Filter instrumentals only |
| duration | string? | — | Filter by duration range |
| sort | string? | — | Sort order |

**Response (200): `PagedResult<TrackResponse>`**
```json
{
  "items": [
    {
      "id": "guid-string",
      "cambrianTrackId": "CAMB-TRK-A1B2C3D4",
      "title": "Midnight Pulse",
      "description": "A cinematic dark synth track",
      "genre": "Electronic",
      "price": 29.99,
      "nonExclusivePrice": 29.99,
      "exclusivePrice": 199.99,
      "copyrightBuyoutPrice": 499.99,
      "platformFeePercent": 0.15,
      "nonExclusivePlatformFee": 4.50,
      "nonExclusiveCreatorEarnings": 25.49,
      "exclusivePlatformFee": 30.00,
      "exclusiveCreatorEarnings": 169.99,
      "copyrightBuyoutPlatformFee": 75.00,
      "copyrightBuyoutCreatorEarnings": 424.99,
      "exclusiveSold": false,
      "status": "available",
      "copyrightOwnerId": null,
      "licenseType": "non-exclusive",
      "duration": "3:42",
      "audioUrl": "/stream/guid/audio",
      "coverArtUrl": "https://r2.example.com/covers/cover.jpg",
      "creatorId": "creator-user-id",
      "mood": "dark",
      "tempo": "128 BPM",
      "tags": ["cinematic", "synth", "dark"],
      "instrumental": true,
      "visibility": "public",
      "creatorSlug": "studio-nova",
      "creatorProfileImageUrl": "https://r2.example.com/profiles/avatar.jpg",
      "artist": "Studio Nova",
      "createdAt": "2026-03-15T10:30:00Z"
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 142,
  "totalPages": 8,
  "hasNextPage": true,
  "hasPreviousPage": false
}
```

### `GET /tracks/{trackId}`

**Response (200):** Single `TrackResponse` (same shape as above).

### `GET /trending`

**Response (200):** Array of `TrackResponse`.

### `GET /tracks`

**Response (200):** Array of `TrackResponse`.

---

## Upload (Authorized, Creator role)

### `POST /upload`

**Request (multipart/form-data):**
| Field | Type | Required | Notes |
|-------|------|----------|-------|
| audio | file | Yes | .mp3/.wav/.flac/.aac/.ogg/.m4a, max 100MB |
| coverArt | file | No | Image, max 10MB |
| title | string | Yes | Max 200 chars |
| description | string | No | |
| genre | string | No | |
| price | decimal | No | Legacy generic price |
| nonExclusivePrice | int | No | Cents |
| exclusivePrice | int | No | Cents |
| copyrightBuyoutPrice | int | No | Cents |
| licenseType | string | No | |
| tags | string | No | Comma-separated |

**Response (201): `UploadTrackResponse`**
```json
{
  "trackId": "guid-string",
  "title": "Midnight Pulse",
  "cambrianTrackId": "CAMB-TRK-A1B2C3D4"
}
```

---

## Checkout

### `POST /checkout` (Authorized)

**Request:**
```json
{
  "trackId": "guid-or-camb-trk-id",
  "licenseType": "non-exclusive",
  "usageType": "youtube"
}
```

**Response (200):**
```json
{
  "checkoutUrl": "https://checkout.stripe.com/...",
  "status": "pending"
}
```

### `GET /checkout/session/{sessionId}` (Authorized)

**Response (200): `CheckoutConfirmResponse`**
```json
{
  "status": "completed",
  "trackId": "guid-string",
  "licenseType": "non-exclusive",
  "addedToLibrary": true,
  "sessionId": "cs_xxx",
  "licenseId": "license-guid"
}
```

---

## Library (Authorized)

### `GET /library`

**Response (200):** Array of `LibraryItemResponse`
```json
[
  {
    "trackId": "guid-string",
    "id": "guid-string",
    "title": "Midnight Pulse",
    "artist": "Studio Nova",
    "purchased": true,
    "purchasedOn": "2026-03-15T10:30:00Z",
    "audioUrl": "/stream/guid/audio",
    "genre": "Electronic"
  }
]
```

### `POST /library`

**Request:**
```json
{
  "trackId": "guid-string",
  "title": "Midnight Pulse",
  "artist": "Studio Nova",
  "audioUrl": "/stream/guid/audio"
}
```

**Response (200):**
```json
{ "message": "Saved to library" }
```

### `DELETE /library/{trackId}` (Authorized)

**Response (200):**
```json
{ "message": "Removed from library" }
```

### `POST /library/{trackId}` (Authorized)

**Response (200):**
```json
{ "message": "Added to library" }
```

### `GET /library/purchased-track-ids` (Authorized)

**Response (200):** Array of string track IDs.

---

## Billing (Authorized)

### `POST /billing/checkout` | `POST /billing/checkout-session`

**Request:**
```json
{ "tier": "pro" }
```

**Response (200):**
```json
{ "checkoutUrl": "https://checkout.stripe.com/..." }
```

### `GET /billing/status`

**Response (200): `BillingStatusResponse`**
```json
{
  "tier": "pro",
  "status": "Active",
  "expiresAt": "2026-04-19T00:00:00Z",
  "creatorTier": "Pro",
  "uploadCount": 5,
  "uploadLimit": null,
  "platformFeePercent": 0.15
}
```

### `GET /billing/checkout-session/{sessionId}`

**Response (200): `CheckoutSessionStatusResponse`**
```json
{
  "status": "paid",
  "tier": "pro",
  "sessionId": "cs_xxx"
}
```

---

## Payments (Authorized)

### `POST /payments/checkout`

**Request:**
```json
{
  "trackId": "guid-string",
  "licenseType": "exclusive",
  "usageType": "film"
}
```

**Response (200):**
```json
{ "checkoutUrl": "https://checkout.stripe.com/..." }
```

### `GET /payments/state`

**Response (200): `PaymentStateResponse`**
```json
{
  "status": "idle",
  "purchaseIds": [],
  "processedEventIds": []
}
```

### `GET /payments/result`

**Query: `?status=completed&trackId=guid`**

**Response (200): `PaymentResultResponse`**
```json
{
  "status": "completed",
  "purchaseId": "guid",
  "eventId": "evt_xxx",
  "eventType": "checkout.session.completed",
  "duplicate": false
}
```

### `POST /payments/process`

**Request:**
```json
{
  "purchaseId": "guid",
  "paymentMethodId": "pm_xxx"
}
```

### `POST /purchases` (Authorized)

**Request:**
```json
{
  "trackId": "guid-string",
  "licenseType": "non-exclusive",
  "stripeSessionId": "cs_xxx",
  "usageType": "personal"
}
```

**Response (200): `PurchaseResponse`**
```json
{
  "id": "purchase-guid",
  "trackId": "guid-string",
  "trackTitle": "Midnight Pulse",
  "amountCents": 2999,
  "currency": "usd",
  "licenseType": "non-exclusive",
  "status": "completed",
  "createdAt": "2026-03-15T10:30:00Z",
  "completedAt": "2026-03-15T10:31:00Z"
}
```

### `POST /purchases/credit-creator` (Authorized)

**Request:**
```json
{
  "creatorId": "user-id",
  "trackId": "guid-string",
  "trackTitle": "Midnight Pulse",
  "amountCents": 2549,
  "licenseType": "non-exclusive"
}
```

---

## Downloads (Authorized)

### `GET /download/{trackId}`

**Response (200):**
```json
{
  "url": "https://r2.example.com/signed/...",
  "filename": "midnight-pulse.mp3",
  "licenseId": "license-guid",
  "expiresAt": "2026-03-15T11:30:00Z"
}
```

### `GET /download/{trackId}/file`

**Response:** Binary audio stream with `Content-Disposition` header.

### `GET /download/{trackId}/signed`

**Response (200):**
```json
{
  "signedUrl": "https://r2.example.com/signed/...",
  "expiresAt": "2026-03-15T11:30:00Z"
}
```

---

## Streaming (Authorized)

### `GET /stream`

**Query: `?take=20`**

**Response (200):**
```json
[
  {
    "id": "guid",
    "title": "Midnight Pulse",
    "artist": "Studio Nova",
    "genre": "Electronic",
    "duration": "3:42",
    "audioUrl": "/stream/guid/audio"
  }
]
```

### `GET /stream/{trackId}`

**Response (200):**
```json
{
  "trackId": "guid",
  "streamUrl": "https://r2.example.com/signed/..."
}
```

### `GET /stream/{trackId}/audio`

**Response:** 302 redirect to signed storage URL.

### `POST /stream/start`

**Request:**
```json
{
  "trackId": "guid",
  "title": "Midnight Pulse"
}
```

**Response (200):**
```json
{
  "streamId": "session-guid",
  "status": "started"
}
```

### `POST /stream/stop`

**Query: `?streamId=session-guid`**

**Response (200):**
```json
{ "status": "stopped" }
```

---

## Creator Profile

### `GET /creator-profile/{slug}` (Public)

**Response (200): `CreatorProfileDto`**
```json
{
  "id": "profile-guid",
  "userId": "user-id",
  "slug": "studio-nova",
  "bio": "Cinematic AI composer",
  "niche": "cinematic",
  "profileImageUrl": "https://r2.example.com/avatars/...",
  "bannerImageUrl": "https://r2.example.com/banners/...",
  "socialLinks": [
    { "platform": "twitter", "url": "https://twitter.com/studionova" }
  ],
  "stats": {
    "totalDownloads": 150,
    "totalEarnings": 4500.00
  },
  "showEarnings": true,
  "showDownloadStats": true,
  "pinnedTrackIds": "guid1,guid2,guid3",
  "createdAt": "2026-01-15T00:00:00Z",
  "updatedAt": "2026-03-10T00:00:00Z"
}
```

### `GET /creator-profile/{slug}/storefront` (Public)

**Response (200): `StorefrontResponse`**
```json
{
  "profile": { /* CreatorProfileDto */ },
  "stats": { "totalDownloads": 150, "totalEarnings": 4500.00 },
  "pinnedTracks": [ /* TrackResponse[] */ ],
  "collections": [ /* TrackCollectionDto[] */ ],
  "tracks": [ /* TrackResponse[] */ ]
}
```

### `GET /creator-profile/me` (Authorized)

**Response (200):** `CreatorProfileDto` or `{ "exists": false }`.

### `PUT /creator-profile/me` (Authorized)

**Request:**
```json
{
  "slug": "studio-nova",
  "bio": "Cinematic AI composer",
  "niche": "cinematic",
  "socialLinks": [
    { "platform": "twitter", "url": "https://twitter.com/studionova" }
  ],
  "showEarnings": true,
  "showDownloadStats": true
}
```

**Response (200):** `CreatorProfileDto`.

### `POST /creator-profile/me/banner` (Authorized, multipart)

**Response (200):**
```json
{ "bannerImageUrl": "https://r2.example.com/banners/..." }
```

### `POST /creator-profile/me/avatar` (Authorized, multipart)

**Response (200):**
```json
{ "profileImageUrl": "https://r2.example.com/avatars/..." }
```

### `GET /creator-profile/{slug}/collections`

**Response (200):** Array of `TrackCollectionDto`.

### `POST /creator-profile/me/collections` (Authorized)

**Request:**
```json
{
  "title": "My Best Tracks",
  "description": "Curated collection",
  "trackIds": ["guid1", "guid2"]
}
```

**Response (201):** `TrackCollectionDto`.

### `PUT /creator-profile/me/collections/{collectionId}` (Authorized)

Same request shape as POST. **Response (200):** `TrackCollectionDto`.

### `DELETE /creator-profile/me/collections/{collectionId}` (Authorized)

**Response (204):** No content.

### `PUT /creator-profile/me/pinned-tracks` (Authorized)

**Request:**
```json
{ "trackIds": ["guid1", "guid2", "guid3"] }
```

**Response (200):**
```json
{ "pinnedTrackIds": "guid1,guid2,guid3" }
```

---

## Creator Dashboard (Authorized, Creator role)

### `GET /creator/tracks`

**Query: `?page=1&pageSize=20`**

**Response (200):** Paginated `TrackResponse[]`.

### `GET /creator/revenue`

**Response (200):** Creator revenue summary object.

---

## Licenses (Authorized)

### `GET /licenses`

**Response (200):** Array of `LicenseCertificateDto`
```json
[
  {
    "licenseId": "license-guid",
    "trackId": "CAMB-TRK-A1B2C3D4",
    "licenseType": "non-exclusive",
    "buyerId": "buyer-user-id",
    "creatorId": "creator-user-id",
    "usageType": "youtube",
    "issuedAt": "2026-03-15T10:31:00Z",
    "allowedUses": ["youtube", "social"],
    "restrictions": ["No redistribution"],
    "copyrightOwner": "Studio Nova"
  }
]
```

### `GET /licenses/{licenseId}`

**Response (200):** Single `LicenseCertificateDto`.

### `GET /licenses/{licenseId}/pdf`

**Response:** Binary PDF file stream.

---

## Invoices (Authorized)

### `GET /invoices`

**Response (200):** Array of `InvoiceResponse`
```json
[
  {
    "id": "invoice-guid",
    "purchaseId": "purchase-guid",
    "amountCents": 2999,
    "currency": "usd",
    "status": "paid",
    "issuedAt": "2026-03-15T10:31:00Z",
    "paidAt": "2026-03-15T10:31:00Z"
  }
]
```

### `GET /invoices/{invoiceId}`

**Response (200):** Single `InvoiceResponse`.

### `GET /invoices/{invoiceId}/download`

**Response:** Binary PDF file stream.

---

## Wallet (Authorized)

### `GET /wallet`

**Response (200): `WalletResponse`**
```json
{
  "balanceCents": 25490,
  "currency": "usd"
}
```

### `POST /wallet/withdraw`

**Request:**
```json
{ "amount": 100.00 }
```

**Response (200):**
```json
{ "status": "processing" }
```

### `GET /wallet/history`

**Response (200):** Array of `WalletTransactionResponse`
```json
[
  {
    "id": "tx-guid",
    "amountCents": 2549,
    "type": "credit",
    "description": "Sale: Midnight Pulse (non-exclusive)",
    "createdAt": "2026-03-15T10:31:00Z"
  }
]
```

---

## Payouts (Authorized, Creator role)

### `POST /payouts/connect-stripe` | `POST /payouts/connect`

**Response (200):**
```json
{
  "connectUrl": "https://connect.stripe.com/...",
  "status": "onboarding"
}
```

### `GET /payouts/connect-status`

**Response (200):**
```json
{
  "connected": true,
  "accountId": "acct_xxx",
  "status": "active"
}
```

### `GET /payouts/stripe-dashboard`

**Response (200):**
```json
{ "url": "https://dashboard.stripe.com/..." }
```

### `GET /payouts/account`

**Response (200):**
```json
{
  "accountId": "acct_xxx",
  "status": "active"
}
```

### `DELETE /payouts/disconnect` | `POST /payouts/disconnect`

**Response (200):**
```json
{ "message": "Stripe account disconnected" }
```

### `GET /payouts/earnings` | `GET /earnings`

**Response (200):** Creator earnings summary.

### `POST /payouts/request`

**Request:**
```json
{ "amount": 100.00 }
```

**Response (200): `PayoutResponse`**
```json
{
  "amount": 100.00,
  "status": "pending"
}
```

### `GET /payouts/history`

**Query: `?take=20`**

**Response (200):** Array of `PayoutResponse`.

### `POST /payouts/settings` | `PUT /payouts/settings`

**Request:**
```json
{
  "threshold": 50.00,
  "schedule": "monthly"
}
```

---

## Subscriptions (Authorized)

### `GET /subscriptions/plans`

**Response (200):** Array of `PlanResponse`
```json
[
  {
    "name": "pro",
    "description": "Unlimited uploads, 15% platform fee",
    "priceCents": 999,
    "interval": "month",
    "features": ["Unlimited uploads", "15% platform fee", "Priority support"]
  }
]
```

### `GET /subscriptions/current`

**Response (200): `SubscriptionResponse`**
```json
{
  "id": "sub-guid",
  "plan": "pro",
  "status": "active",
  "startedAt": "2026-03-01T00:00:00Z",
  "expiresAt": "2026-04-01T00:00:00Z"
}
```

### `POST /subscriptions/update`

**Request:**
```json
{ "plan": "pro" }
```

**Response (200):** `SubscriptionResponse`.

### `POST /subscriptions/cancel`

**Response (200):**
```json
{ "message": "Subscription cancelled" }
```

### `GET /subscriptions/history`

**Response (200):** Array of `SubscriptionResponse`.

---

## Analytics

### `POST /analytics/track`

**Request:**
```json
{
  "eventType": "play",
  "trackId": "guid-string",
  "metadata": "source=discover"
}
```

**Response (200):**
```json
{ "recorded": true }
```

### `GET /analytics/summary`

**Response (200):** Event counts by type.

### `GET /analytics/events`

**Response (200):** Array of raw analytics events.

---

## Feature Flags

### `GET /feature-flags/check/{name}`

**Response (200):**
```json
{ "name": "new-player", "enabled": true }
```

### `GET /feature-flags`

**Response (200):** Array of all feature flags.

### `PUT /feature-flags/{name}`

**Request:**
```json
{ "enabled": true, "rolloutPercentage": 50 }
```

### `DELETE /feature-flags/{name}`

**Response (204):** No content.

---

## Webhooks

### `POST /webhook/stripe`

**Headers:** `Stripe-Signature` (required)

**Body:** Raw Stripe event JSON.

**Response:** `200 OK` on success, `400` on invalid signature.

---

## Admin (Authorized, Admin role)

### `GET /admin/dashboard`
Returns `AdminDashboardSummary`: totalUsers, activeCreators, tracksUploaded, licensesSold, totalRevenue, pendingPayouts.

### `GET /admin/audit`
Returns array of `AdminAuditLog`: id, action, admin, timestamp, details.

### `GET /admin/integrity`
Returns `IntegrityReport`: generatedAt, totalViolations, violations[], summary.

### `GET /admin/users`
Returns array of `AdminUser`: id, email, displayName, role, status, tier, verifiedCreator, creatorTier, uploadCount, createdAt.

### `GET /admin/tracks`
Returns array of `AdminTrack`: id, title, genre, creatorId, creatorEmail, status, visibility, nonExclusivePriceCents, exclusivePriceCents, copyrightBuyoutPriceCents, createdAt.

### `GET /admin/purchases`
Returns array of `AdminPurchase`: id, buyerId, buyerEmail, trackId, trackTitle, amountCents, licenseType, status, completedAt, createdAt.

### `GET /admin/payouts`
Returns all payouts (all statuses) as `AdminPayout[]`: id, creatorId, creatorEmail, amountCents, status, requestedAt, completedAt.

### `GET /admin/settings` | `POST /admin/settings`
GET returns live values from `TierManifest` (freeTierFeePercent, proTierFeePercent, proTierPriceCents, freeTierUploadLimit, proTierUploadLimit, featureToggles).
POST returns **501** — settings persistence not yet implemented.

### `GET /admin/payouts/requests`
Returns pending payout requests.

### `POST /admin/payouts/{id}/approve` | `POST /admin/payouts/{id}/reject`
Approve or reject payout.

### `POST /admin/users/{id}/role`
**Request:** `{ "role": "Creator" }`

### `POST /admin/users/{id}/suspend`
**Request:** `{ "reason": "Policy violation" }`

### `POST /admin/users/{id}/reactivate`
Reactivate suspended user.

### `POST /admin/users/{id}/reset-password`
Admin-initiated password reset.

### `POST /admin/users/{id}/verify-creator`
Mark user as verified creator.

### `GET /admin/reports`
List content abuse reports.

### `POST /admin/reports/{id}/investigate`
Begin investigation on report.

### Track Moderation
- `POST /admin/tracks/{id}/remove`
- `POST /admin/tracks/{id}/restore`
- `POST /admin/tracks/{id}/hide`
- `POST /admin/tracks/{id}/flag`
- `POST /admin/tracks/{id}/feature`
- `POST /admin/tracks/{id}/pin`
- `POST /admin/tracks/{id}/visibility` — Request: `{ "visibility": "hidden" }`

### `POST /admin/collections/curate`
Curate collections.

### `POST /admin/tags/manage`
Manage tags.

### `POST /admin/purge-test-data` (Staging only)
**Query:** `?confirm=yes` — Blocked in production. Returns `PurgeResult`.

---

## Community

### `GET /community`

**Query: `?page=1&pageSize=20`**

**Response (200):** Community feed items.

---

## Tiers

### `GET /tiers/config`

**Response (200):** Tier configuration (limits, fees, prices).

---

## Health

### `GET /health`

**Response (200):**
```json
{ "status": "healthy", "environment": "Production" }
```

### `GET /health/storage`

**Response (200):** Storage diagnostics.

---

## Debug (Admin only)

### `GET /debug/user/{userId}`
Full user state (profile, tier, subscription, purchases, library).

### `GET /debug/webhooks`
**Query:** `?limit=20&eventType=checkout.session.completed`

### `GET /debug/consistency`
Library consistency check.

---

## Data (Authorized)

### `GET /data/account`
User account info: id, email, plan, region, status.

### `GET /data/songs` | `POST /data/songs` (Admin)
Song data management.

### `GET /data/system` | `POST /data/system` (Admin)
System data management.

### `GET /data/secrets` | `POST /data/secrets` (Admin)
Secret management.

---

## AI

### `GET /ai/playlist` (Authorized)
**Query:** `?seedTrackId=guid`

### `POST /generate` (Authorized)
AI track generation.

---

## Enums Reference

### License Types
`standard`, `non-exclusive`, `exclusive`, `copyright_buyout`

### Usage Types
`personal`, `youtube`, `ads`, `podcast`, `game`, `film`, `social`

### Creator Tiers
| Tier | Upload Limit | Platform Fee | Price |
|------|-------------|--------------|-------|
| Free | 10 tracks | 35% | $0 |
| Pro | Unlimited | 15% | $9.99/mo |

### Track Status
`available`, `exclusive_sold`, `copyright_transferred`

### Purchase Status
`pending`, `completed`, `refunded`

### Subscription Status
`Active`, `Inactive`, `Cancelled`

### Payout Status
`pending`, `approved`, `rejected`, `completed`
