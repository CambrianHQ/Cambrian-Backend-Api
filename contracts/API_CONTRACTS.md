# API Contracts

> Canonical source of truth for all API endpoint shapes.
> Any change to request/response shapes MUST be reflected here FIRST.
> Last updated: 2026-03-20 | Contract version: 2.3.0

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

Issues the request token and matching HttpOnly antiforgery cookie used by
cookie-authenticated state-changing requests. Send the token in
`X-CSRF-TOKEN` and include a trusted `Origin` or `Referer`.

**Response (200):**
```json
{ "token": "...", "headerName": "X-CSRF-TOKEN" }
```

### `POST /auth/forgot-password`

### `GET /auth/google/status`

**Response (200):**
```json
{
  "configured": true,
  "clientIdPrefix": "12345678..."
}
```

### `POST /auth/google`

**Request:**
```json
{ "idToken": "google-id-token" }
```

**Response (200):** Same session shape as login/register.

### `POST /auth/set-password` (Authorized)

Set a local password for an authenticated account that does not already have one.

**Request:**
```json
{ "password": "NewSecure456!" }
```

**Response (200):**
```json
{ "message": "Password set successfully." }
```

### `POST /auth/link-google` (Authorized)

Link a Google account to the authenticated user when the Google email matches the local account email.

**Request:**
```json
{ "idToken": "google-id-token" }
```

**Response (200):**
```json
{ "message": "Google account linked successfully." }
```

### `POST /auth/refresh` (Authorized)

Returns a fresh JWT with updated claims.

**Response (200):**
```json
{ "token": "eyJ..." }
```

### `POST /auth/set-username` (Authorized)

Set the current user's creator username during onboarding. This may promote a `User` to the `Creator` role.

**Request:**
```json
{ "username": "studio-nova" }
```

**Response (200):**
```json
{
  "username": "studio-nova",
  "displayName": "Studio Nova",
  "role": "Creator",
  "token": "eyJ..."
}
```

### `GET /auth/username-availability`

**Query:** `?username=studio-nova`

**Response (200):**
```json
{
  "username": "studio-nova",
  "available": true
}
```

**Request:**
**Response (200):**
```json
{ "message": "Code verified successfully." }
```
**Response (200):**
### `POST /settings/profile/avatar` (Authorized)
{ "message": "If the account exists, a reset code has been sent." }
```

### `POST /auth/verify-code`
**Request:**
```json
Upload an image to object storage and receive back its current access URL. Under the current implementation this URL may be signed and time-limited rather than a permanent public URL. Use it only if that behavior is acceptable for the calling flow.
```
**Current flow:** Upload → get URL → `PATCH /users/me` with URL
**Response (200):**

### `POST /api/uploads/creator-image-url` (Authorized, Creator role)

Create a five-minute, one-time upload grant for the current creator. The grant
binds the user, exact key, purpose, MIME type, size limit, expiry, and nonce.

**Request:**
```json
{
  "type": "profile",
  "fileName": "avatar.png",
  "contentType": "image/png"
}
```

**Response (200):**
```json
{
  "uploadUrl": "https://cambrian-backend-api.onrender.com/api/uploads/creator-image/creator-profiles/{userId}/{nonce}.png?grant=...",
  "publicUrl": "https://cdn.example.com/creator-profiles/{userId}/{nonce}.png",
  "expiresAt": "2026-06-18T12:05:00Z"
}
```

### `PUT /api/uploads/creator-image/{**key}` (Authorized)

Consumes the server-issued one-time grant. Caller-selected keys, cross-account
keys, expired/replayed grants, invalid MIME/magic bytes, oversized bodies, path
traversal, and excessive dimensions are rejected.

**Response (200):**
```json
{ "publicUrl": "https://cdn.example.com/creator-profiles/{userId}/{nonce}.png" }
```

### `POST /api/uploads/creator-image` (Authorized, Creator role)

Multipart fallback for creator profile or cover image uploads.

**Query:** `?type=profile` or `?type=cover`

**Response (200):**
```json
{
  "uploadUrl": "https://cdn.example.com/creator-profiles/...",
  "publicUrl": "https://cdn.example.com/creator-profiles/..."
}
```
```json
{ "valid": true }
```

### `POST /auth/reset-password`


### `GET /creator-profile/{slug}/follow` (Authorized)

**Response (200):**
```json
{ "following": true, "followerCount": 42 }
```

### `POST /creator-profile/{slug}/follow` (Authorized)

**Response (200):**
```json
{ "following": true, "followerCount": 42 }
```

### `DELETE /creator-profile/{slug}/follow` (Authorized)

**Response (200):**
```json
{ "following": false, "followerCount": 41 }
```
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

**Response (200):**
```json
{
  "displayName": "DJ Example",
  "email": "dj@example.com",
  "tier": "free",
  "role": "Creator",
  "profileImageUrl": "https://cdn.example.com/avatars/abc123.jpg",
  "coverImageUrl": null,
  "bio": "Producer from LA."
}
```

> `profileImageUrl`, `coverImageUrl`, and `bio` are `null` if not yet set. These fields are read from `AspNetUsers` and updated via `PATCH /users/me`.

### `POST /settings/profile/avatar` (Authorized — Creator role)

Upload or replace a creator's profile photo from the settings area.

**Request:** `multipart/form-data`
| Field | Type | Notes |
|-------|------|-------|
| file | IFormFile | jpg, jpeg, png, webp — max 10 MB |

**Response (200):**
```json
{ "profileImageUrl": "https://cdn.example.com/avatars/abc123.jpg" }
```

**Errors:**
| Status | Reason |
|--------|--------|
| 400 | Invalid or missing file / wrong type / too large |
| 403 | User is not a Creator |

> Also accessible at `POST /creator-profile/me/avatar` — identical behaviour.

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

## User Profiles

### `GET /users/:username` (Public)

Fetch any user's public profile by their username, including their public track catalogue.

**Response (200):**
```json
{
  "username": "djexample",
  "displayName": "DJ Example",
  "profileImageUrl": "https://cdn.example.com/images/abc.jpg",
  "coverImageUrl": "https://cdn.example.com/images/xyz.jpg",
  "bio": "Producer from LA.",
  "role": "Creator",
  "verifiedCreator": true,
  "tracks": [
    {
      "id": "00000000-0000-0000-0000-000000000000",
      "title": "Midnight Drive",
      "genre": "Hip-Hop",
      "coverArtUrl": "https://cdn.example.com/covers/t.jpg",
      "nonExclusivePriceCents": 2999,
      "exclusivePriceCents": 19900,
      "copyrightBuyoutPriceCents": 49900,
      "createdAt": "2026-01-15T10:00:00Z"
    }
  ]
}
```

**Errors:**
| Status | Reason |
|--------|--------|
| 404 | Username not found |

---

### `PATCH /users/me` (Authorized)

Update the authenticated user's own profile fields. All fields are optional — only provided fields are updated.

**Request:**
```json
{
  "profileImageUrl": "https://cdn.example.com/images/abc.jpg",
  "coverImageUrl": "https://cdn.example.com/images/xyz.jpg",
  "bio": "Producer from LA."
}
```

**Response (200):**
```json
{
  "username": "djexample",
  "displayName": "DJ Example",
  "profileImageUrl": "https://cdn.example.com/images/abc.jpg",
  "coverImageUrl": "https://cdn.example.com/images/xyz.jpg",
  "bio": "Producer from LA."
}
```

**Validation:**
- `bio`: max 500 characters
- `profileImageUrl` / `coverImageUrl`: stored as-is; use `POST /uploads/image` to get a URL first

**Errors:**
| Status | Reason |
|--------|--------|
| 400 | Bio exceeds 500 characters |
| 401 | Not authenticated |

---

### `POST /uploads/image` (Authorized)

Upload an image to object storage and receive back its URL. Use the URL to update a profile via `PATCH /users/me`.

**Recommended flow:** Upload → get URL → `PATCH /users/me` with URL

**Request:** `multipart/form-data`
| Field | Type | Notes |
|-------|------|-------|
| file | IFormFile | jpg, jpeg, png, webp — max 10 MB |

**Response (200):**
```json
{ "url": "https://cdn.example.com/images/abc123.jpg" }
```

**Errors:**
| Status | Reason |
|--------|--------|
| 400 | No file / wrong type / exceeds 10 MB |
| 401 | Not authenticated |

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

**Response (200): paginated catalog envelope**
```json
{
  "success": true,
  "data": [
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

### `PUT /creator/tracks/{trackId}` (Authorized, Creator role)

Update editable track metadata and pricing.

**Request:**
```json
{
  "title": "Midnight Pulse",
  "description": "Updated description",
  "primaryGenre": "Electronic",
  "subgenre": "Synthwave",
  "mood": "dark",
  "tempo": "128 BPM",
  "tags": "cinematic,synth,dark",
  "nonExclusivePriceCents": 999,
  "exclusivePriceCents": 4999,
  "copyrightBuyoutPriceCents": 19999
}
```

**Response (200):** Track mutation payload including both dollar aliases and cent fields.

### `PUT /creator/tracks/{trackId}/cover-art` (Authorized, Creator role)

Replace an existing song's cover image without deleting or re-uploading the track.

**Request (multipart/form-data):**
| Field | Type | Required | Notes |
|-------|------|----------|-------|
| coverArt | file | Yes | Image, max 10MB |

**Response (200):** Track mutation payload including the updated `coverArtUrl`.

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
{ "status": "ok" }
```

### `GET /health/details` (Admin only)

Detailed dependency health.

### `GET /health/storage` (Admin only)

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
`pending`, `completed`, `failed`
