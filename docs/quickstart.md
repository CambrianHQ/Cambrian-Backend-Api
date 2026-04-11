# 5-minute quickstart

Go from zero to licensing a track in five minutes. You'll create an API key, browse the catalogue, inspect a track, and generate a Stripe checkout URL to buy a license.

**Base URL:** `https://api.cambrianmusic.com`
**Interactive explorer:** [api-explorer.html](./api-explorer.html)
**OpenAPI spec:** `https://api.cambrianmusic.com/openapi.json`

---

## 1. Get an API key (30 seconds)

API keys are managed from the Cambrian dashboard and require a logged-in platform account.

1. Sign up or log in at [cambrianmusic.com](https://cambrianmusic.com).
2. Go to **Dashboard → API Keys** (`/dashboard/api-keys`).
3. Click **Create key**, give it a name (e.g. `local-dev`), and copy the raw key.

Keys look like `cbr_` followed by 64 hex characters:

```
cbr_7a3b9f1c4e8d2b6a0f5c9e1d3a7b4c8e2f6a9d1b5c8e7f3a0d4b9c2e6f1a5d8
```

> The raw key is shown **once** at creation. Cambrian only stores a SHA-256 hash, so if you lose it you'll need to revoke the key and create a new one. Treat it like a password; never commit it to version control.

Send it with every request as the `X-API-Key` header:

```
X-API-Key: cbr_7a3b9f1c4e8d2b6a0f5c9e1d3a7b4c8e2f6a9d1b5c8e7f3a0d4b9c2e6f1a5d8
```

Rate limit: **100 requests per minute** per key (IP-based for anonymous endpoints). Exceeding it returns HTTP 429.

---

## 2. List tracks (30 seconds)

The track catalogue is public — you don't actually need a key to browse it, but passing one makes rate limiting partition by key rather than shared-IP.

```bash
curl "https://api.cambrianmusic.com/api/v1/tracks?limit=3" \
  -H "X-API-Key: $CAMBRIAN_API_KEY"
```

```json
{
  "success": true,
  "data": [
    {
      "id": "3f9d2a1b-4c8e-4a7d-9b6f-1e2d3c4b5a6f",
      "cambrianTrackId": "CAMB-TRK-A1B2C3D4",
      "title": "Sunset Drive",
      "genre": "Synthwave",
      "mood": "Nostalgic",
      "tempo": "110 BPM",
      "instrumental": true,
      "tags": ["retro", "driving", "chill"],
      "duration": "3:42",
      "coverArtUrl": "https://cdn.cambrianmusic.com/covers/...",
      "audioUrl": "https://api.cambrianmusic.com/stream/3f9d2a1b.../audio",
      "artist": "Neon Halo",
      "creatorId": "usr_neonhalo",
      "creatorSlug": "neonhalo",
      "price": 29.00,
      "nonExclusivePrice": 29.00,
      "exclusivePrice": 499.00,
      "copyrightBuyoutPrice": 1999.00,
      "visibility": "public",
      "status": "available",
      "createdAt": "2026-01-15T10:22:04Z"
    }
  ],
  "meta": { "page": 1, "limit": 3, "total": 412, "totalPages": 138, "hasNext": true, "hasPrev": false }
}
```

> **Prices are decimal dollars, not integer cents.** `nonExclusivePrice: 29.00` means $29.00. There's no `/100` conversion — display it as-is.

### Filter by mood, genre, and tempo

```bash
curl "https://api.cambrianmusic.com/api/v1/tracks?\
mood=chill&genre=lofi&instrumental=true&limit=10" \
  -H "X-API-Key: $CAMBRIAN_API_KEY"
```

Supported query parameters:

| Param          | Type    | Example                          |
| -------------- | ------- | -------------------------------- |
| `search`       | string  | `search=sunset drive`            |
| `genre`        | string  | `genre=lofi`                     |
| `mood`         | string  | `mood=chill`                     |
| `tempo`        | string  | `tempo=slow`                     |
| `instrumental` | bool    | `instrumental=true`              |
| `sort`         | string  | `sort=trending` / `sort=newest`  |
| `page`         | int     | `page=1` (default)               |
| `limit`        | int     | `limit=20` (default, max 100)    |

---

## 3. Get one track (30 seconds)

```bash
curl "https://api.cambrianmusic.com/api/v1/tracks/CAMB-TRK-A1B2C3D4" \
  -H "X-API-Key: $CAMBRIAN_API_KEY"
```

The response wraps a single track in `{ success, data }`. You can pass either the short `CAMB-TRK-XXXX` identifier or the internal UUID.

---

## 4. Initiate a license purchase (2 minutes)

This is the one V1 endpoint that **requires authentication** — pass your API key. It returns a Stripe-hosted checkout URL that you redirect the buyer to.

```bash
curl -X POST "https://api.cambrianmusic.com/api/v1/licenses" \
  -H "X-API-Key: $CAMBRIAN_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "trackId": "CAMB-TRK-A1B2C3D4",
    "licenseType": "non-exclusive",
    "usageType": "youtube",
    "clientReferenceId": "order-12345"
  }'
```

```json
{
  "success": true,
  "checkoutUrl": "https://checkout.stripe.com/c/pay/cs_test_a1b2c3...",
  "status": "created"
}
```

**Request fields:**

| Field               | Required | Values                                                                  |
| ------------------- | -------- | ----------------------------------------------------------------------- |
| `trackId`           | yes      | `CAMB-TRK-XXXX` or UUID                                                  |
| `licenseType`       | yes      | `standard` · `non-exclusive` · `exclusive` · `copyright_buyout`          |
| `usageType`         | no       | `personal` · `youtube` · `ads` · `podcast` · `game` · `film` · `social`  |
| `clientReferenceId` | no       | Any string you want round-tripped — useful for linking orders internally |

Redirect the buyer to `checkoutUrl`. When they finish paying, Stripe fires a webhook at Cambrian, which issues a `LicenseCertificate`. Stripe then returns the buyer to `{frontend}/marketplace?view=success&trackId={id}&session_id={sessionId}`.

> **License types at a glance.**
> **`non-exclusive`** — the most common: buyer can use the track, creator can keep selling it. **`exclusive`** — only the buyer can use it going forward; the listing is marked sold. **`copyright_buyout`** — full copyright transfer; the creator loses the listing. **`standard`** is a legacy alias for `non-exclusive`.

---

## 5. Verify a license (30 seconds)

After checkout completes, you (or anyone) can confirm a license is valid. This endpoint is public — no key needed — so it's safe to expose in client-side verification flows.

```bash
curl "https://api.cambrianmusic.com/api/v1/licenses/LIC-7F3A9B2C/verify"
```

```json
{
  "success": true,
  "data": {
    "licenseId": "LIC-7F3A9B2C",
    "trackId": "CAMB-TRK-A1B2C3D4",
    "licenseType": "non-exclusive",
    "usageType": "youtube",
    "buyerId": "usr_12345",
    "issuedAt": "2026-04-09T18:42:11Z",
    "valid": true
  }
}
```

---

## What about downloading the audio?

The V1 public API covers **discovery, license initiation, and verification**. The actual high-quality audio download is served to authenticated platform users through the internal `GET /download/{trackId}` endpoint, which requires the buyer to be logged in with a JWT (not an API key). For typical integrations, the flow is:

1. Your app calls `POST /api/v1/licenses` with an API key.
2. The buyer completes Stripe checkout in the browser.
3. They return to Cambrian's marketplace (`/marketplace?view=success...`) and log in to download the licensed file.

If you need programmatic audio access for an AI agent or embedded player, use the **[MCP server](./tutorials/build-music-aware-mcp-agent.md)** — its `get_track_preview` tool returns a direct preview URL suitable for inline playback without a separate auth dance.

---

## Where to go next

- **[Interactive API explorer](./api-explorer.html)** — Swagger UI loaded against the live API. Paste your key and try requests in the browser.
- **[Tutorial: Add licensed music to AI-generated video](./tutorials/add-licensed-music-to-ai-video.md)** — end-to-end integration for AI video tools.
- **[Tutorial: Build a music-aware AI agent with MCP](./tutorials/build-music-aware-mcp-agent.md)** — connect Claude / Cursor / any MCP client to Cambrian's discovery tools.
- **[Tutorial: License music programmatically in your content creation tool](./tutorials/license-music-programmatically.md)** — full V1 API integration in a creator app.

### Troubleshooting

| Symptom                                              | Fix                                                                                         |
| ---------------------------------------------------- | -------------------------------------------------------------------------------------------- |
| `401 Unauthorized — Invalid or revoked API key`      | Key was deleted, rotated, or never saved. Create a new one in the dashboard.                 |
| `400 — LicenseType must be…`                         | Use `non-exclusive` (hyphenated), not `nonexclusive`.                                         |
| `429 Too Many Requests`                              | You hit the 100 req/min limit. Back off and retry with exponential jitter.                   |
| `POST /api/v1/licenses` returns `403`                | Your key is valid but the call hit an authorization edge case — double-check the `trackId`. |
| Stripe checkout URL gives "session expired"          | Checkout sessions expire after ~24h. Create a new one via `POST /api/v1/licenses`.           |
