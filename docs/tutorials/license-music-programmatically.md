# License music programmatically in your content creation tool

**For:** Developers building content creation tools — podcast editors, video editors, presentation builders, slideshow apps, social scheduling tools, anywhere users need background music and can't ship without rights.
**Time:** 20 minutes.
**You'll build:** A complete integration — catalogue browse, track selection, license purchase, and post-checkout verification — wired into a typical creator-tool backend.

---

## The shape of the integration

Every content tool that needs licensed music ends up with roughly the same six moving parts. Here's the full picture, with every Cambrian API call and where it fits:

```
┌─────────────┐                ┌──────────────┐               ┌─────────┐
│   Your app  │                │ Cambrian API │               │  Stripe │
└──────┬──────┘                └───────┬──────┘               └────┬────┘
       │                               │                           │
       │  1. GET /api/v1/tracks        │                           │
       │  (browse / search)            │                           │
       ├──────────────────────────────▶│                           │
       │                               │                           │
       │  2. GET /api/v1/tracks/{id}   │                           │
       │  (track detail view)          │                           │
       ├──────────────────────────────▶│                           │
       │                               │                           │
       │  3. POST /api/v1/licenses     │                           │
       │  { trackId, licenseType,      │                           │
       │    usageType }                │                           │
       ├──────────────────────────────▶│                           │
       │                               │ 4. Create checkout session│
       │                               ├──────────────────────────▶│
       │                               │◀──────────────────────────┤
       │  { checkoutUrl }              │                           │
       │◀──────────────────────────────┤                           │
       │                               │                           │
       │  5. Redirect user to          │                           │
       │     checkoutUrl ──────────────────────────────────────────▶│
       │                               │                           │
       │                               │  6. webhook on payment    │
       │                               │◀──────────────────────────┤
       │                               │  (issues LicenseCertificate)
       │                               │                           │
       │  7. GET /api/v1/licenses/     │                           │
       │     {id}/verify               │                           │
       ├──────────────────────────────▶│                           │
       │  { valid: true, … }           │                           │
       │◀──────────────────────────────┤                           │
```

The parts on your side: a browse UI, a pick handler, a checkout redirect, a return handler, and a license record in your database.

---

## Prerequisites

- A Cambrian API key. See the [quickstart](../quickstart.md#1-get-an-api-key-30-seconds).
- Node.js 18+ for the examples (they use built-in `fetch`). Python and cURL equivalents are inline where they clarify.
- A database to store `{ userId, trackId, licenseId, createdAt }` — one row per purchased license.

```bash
export CAMBRIAN_API_KEY="cbr_your_key"
export CAMBRIAN_BASE="https://api.cambrianmusic.com"
```

---

## Step 1 — wrap the API calls in a small client

One file, no dependencies. Keep it boring.

```js
// lib/cambrian.js
const BASE = process.env.CAMBRIAN_BASE || "https://api.cambrianmusic.com";

async function request(path, { method = "GET", body, authed = false } = {}) {
  const headers = { "Content-Type": "application/json" };
  if (authed) headers["X-API-Key"] = process.env.CAMBRIAN_API_KEY;

  const res = await fetch(`${BASE}${path}`, {
    method,
    headers,
    body: body ? JSON.stringify(body) : undefined,
  });

  if (!res.ok) {
    const err = await res.json().catch(() => ({ error: res.statusText }));
    throw new CambrianError(res.status, err.error || "Request failed", err);
  }
  return res.json();
}

export class CambrianError extends Error {
  constructor(status, message, body) {
    super(message);
    this.status = status;
    this.body = body;
  }
}

// ── Public: discovery ────────────────────────────────────────
export const searchTracks = (params = {}) => {
  const qs = new URLSearchParams(
    Object.entries(params).filter(([, v]) => v !== undefined && v !== null)
  );
  return request(`/api/v1/tracks?${qs}`);
};

export const getTrack = (id) => request(`/api/v1/tracks/${encodeURIComponent(id)}`);

export const listGenres = () => request(`/api/v1/genres`);

export const getCreator = (identifier) =>
  request(`/api/v1/creators/${encodeURIComponent(identifier)}`);

// ── Authenticated: license initiation ────────────────────────
export const createLicenseCheckout = (payload) =>
  request("/api/v1/licenses", {
    method: "POST",
    body: payload,
    authed: true,
  });

// ── Public: verification ─────────────────────────────────────
export const verifyLicense = (licenseId) =>
  request(`/api/v1/licenses/${encodeURIComponent(licenseId)}/verify`);
```

That's the whole SDK. Everything downstream is just calling these five functions.

---

## Step 2 — a browse endpoint for your users

Expose `searchTracks` through your own API so the frontend doesn't need to know about Cambrian at all (and so you can cache, log, and add business logic later):

```js
// routes/music/search.js
import { searchTracks } from "../../lib/cambrian.js";

export async function GET(req) {
  const url = new URL(req.url);
  const q = {
    search: url.searchParams.get("q") || undefined,
    genre: url.searchParams.get("genre") || undefined,
    mood: url.searchParams.get("mood") || undefined,
    instrumental: url.searchParams.get("instrumental") === "true" || undefined,
    sort: url.searchParams.get("sort") || "trending",
    page: Number(url.searchParams.get("page") || "1"),
    limit: Math.min(Number(url.searchParams.get("limit") || "20"), 100),
  };

  try {
    const { data, meta } = await searchTracks(q);
    return Response.json({
      tracks: data.map((t) => ({
        id: t.id,
        cambrianId: t.cambrianTrackId, // the CAMB-TRK-XXXX form, friendlier for humans
        title: t.title,
        artist: t.artist ?? "Unknown",
        duration: t.duration,
        coverUrl: t.coverArtUrl,
        mood: t.mood,
        genre: t.genre,
        priceFromUsd: t.nonExclusivePrice ?? t.price ?? null, // dollars, not cents
      })),
      pagination: meta,
    });
  } catch (err) {
    console.error("[cambrian/search]", err);
    return Response.json({ error: "Search failed" }, { status: 502 });
  }
}
```

Notice the projection — you're only exposing what your frontend actually needs. Cambrian's `TrackDto` has more fields, but your users don't care about `CopyrightOwnerId` or `TrendingScore`.

### A track detail endpoint

```js
// routes/music/tracks/[id].js
import { getTrack } from "../../../lib/cambrian.js";

export async function GET(req, { params }) {
  try {
    const { data } = await getTrack(params.id);
    return Response.json({
      id: data.id,
      cambrianId: data.cambrianTrackId,
      title: data.title,
      artist: data.artist,
      creatorId: data.creatorId,
      creatorSlug: data.creatorSlug,
      description: data.description,
      duration: data.duration,
      tempo: data.tempo,
      genre: data.genre,
      mood: data.mood,
      coverUrl: data.coverArtUrl,
      previewUrl: data.audioUrl, // the streamable preview
      // All prices are decimal USD (not cents) — display as-is.
      licenseOptions: {
        nonExclusiveUsd: data.nonExclusivePrice,
        exclusiveUsd: data.exclusivePrice,
        copyrightBuyoutUsd: data.copyrightBuyoutPrice,
      },
    });
  } catch (err) {
    if (err.status === 404) return Response.json({ error: "Not found" }, { status: 404 });
    throw err;
  }
}
```

---

## Step 3 — the purchase handler

When the user clicks "License this track", your backend calls `POST /api/v1/licenses`, stores a pending row in your own database, and returns the Stripe checkout URL.

```js
// routes/music/licenses/create.js
import { createLicenseCheckout } from "../../../lib/cambrian.js";
import { db } from "../../../db/index.js";
import { randomUUID } from "node:crypto";

export async function POST(req) {
  const user = await requireUser(req); // your auth
  const { trackId, licenseType = "non-exclusive", usageType = "personal", projectId } = await req.json();

  // Validate inputs on your side before spending a Cambrian API call.
  const ALLOWED_LICENSE = ["standard", "non-exclusive", "exclusive", "copyright_buyout"];
  const ALLOWED_USAGE = ["personal", "youtube", "ads", "podcast", "game", "film", "social"];
  if (!ALLOWED_LICENSE.includes(licenseType))
    return Response.json({ error: "Invalid licenseType" }, { status: 400 });
  if (!ALLOWED_USAGE.includes(usageType))
    return Response.json({ error: "Invalid usageType" }, { status: 400 });

  // Your own order ID — round-trips through Cambrian as clientReferenceId so
  // you can correlate the purchase back to your project when the user returns.
  const orderId = randomUUID();

  await db.licenseOrders.insert({
    id: orderId,
    userId: user.id,
    projectId,
    trackId,
    licenseType,
    usageType,
    status: "initiated",
    createdAt: new Date(),
  });

  try {
    const { checkoutUrl } = await createLicenseCheckout({
      trackId,
      licenseType,
      usageType,
      clientReferenceId: orderId,
    });

    return Response.json({ orderId, checkoutUrl });
  } catch (err) {
    await db.licenseOrders.update(orderId, { status: "failed" });
    console.error("[cambrian/license]", err);
    return Response.json({ error: err.message }, { status: err.status || 502 });
  }
}
```

Frontend: open `checkoutUrl` in a new tab (or same-tab redirect). The buyer completes the Stripe flow in their own browser context.

> **Why we store the order row before calling Cambrian.** If the Cambrian call fails, you have a record to retry or mark failed. If it succeeds and the user bounces from checkout, you can poll or re-show the checkout URL instead of re-initiating. The `clientReferenceId` field lets you reconcile post-webhook: Cambrian returns it verbatim on the license verify response, so you can match a licenseId → your internal orderId trivially.

---

## Step 4 — handle the return

After the buyer finishes Stripe checkout, Stripe redirects them back to Cambrian's success page (`cambrianmusic.com/marketplace?view=success&trackId=…&session_id=…`). Cambrian's webhook handler issues the `LicenseCertificate` during that redirect window, and the user is free to close the tab and return to your app.

Build a "return to project" button on your side that sends the user to a completion route:

```js
// routes/music/licenses/complete.js
import { verifyLicense } from "../../../lib/cambrian.js";
import { db } from "../../../db/index.js";

export async function GET(req) {
  const url = new URL(req.url);
  const orderId = url.searchParams.get("order");
  const licenseId = url.searchParams.get("license");

  if (!orderId) return Response.json({ error: "Missing order" }, { status: 400 });

  const order = await db.licenseOrders.get(orderId);
  if (!order) return Response.json({ error: "Unknown order" }, { status: 404 });

  // If the caller already has a licenseId from a prior poll, verify it.
  if (licenseId) {
    const { data } = await verifyLicense(licenseId);
    if (!data?.valid) return Response.json({ error: "Invalid license" }, { status: 400 });

    await db.licenseOrders.update(orderId, {
      status: "completed",
      licenseId: data.licenseId,
      completedAt: new Date(),
    });

    return Response.redirect(`/projects/${order.projectId}?licensed=${data.trackId}`);
  }

  // Otherwise show a "waiting for payment" page that polls every few seconds.
  return Response.redirect(`/music/orders/${orderId}/waiting`);
}
```

### How the user gets their licenseId

There are two equally reasonable approaches — pick whichever fits your UX.

**A. Poll the Cambrian verify endpoint.** After checkout, your waiting page hits its own backend every 5 seconds; the backend queries Cambrian for any recent license owned by the buyer. This works well if your users are already authenticated to Cambrian (via your SSO bridge or direct login).

**B. Let the user paste their license ID.** Show a form on the completion page asking for the license ID that was shown on Cambrian's success page. `verifyLicense()` confirms it's real and owned by the right track. Less slick, but zero coordination required.

For most content tools, **B** is the honest path of least resistance. If your volume gets high enough to justify it, upgrade to **A** once you've talked to us about tighter buyer attribution.

---

## Step 5 — store the proof

Once verified, write a row to your `licenses` table. This is what protects your users (and you) in a DMCA dispute.

```sql
CREATE TABLE user_licenses (
  id           uuid PRIMARY KEY,
  user_id      uuid NOT NULL REFERENCES users(id),
  project_id   uuid REFERENCES projects(id),
  cambrian_license_id text NOT NULL UNIQUE,
  cambrian_track_id   text NOT NULL,
  license_type text NOT NULL,
  usage_type   text NOT NULL,
  issued_at    timestamptz NOT NULL,
  verified_at  timestamptz NOT NULL DEFAULT now()
);
```

When the user exports their finished project — the podcast episode, the video, the slide deck — embed the license metadata in the output. All major platforms read some form of this:

- **YouTube:** attach the license info in the description or as a public note
- **Spotify for Podcasters:** include in episode metadata under "music credits"
- **Meta / TikTok:** attach via their rights management portals if you have a partnership, otherwise description text

When a copyright claim fires, you can respond in under a minute with a direct URL to `/api/v1/licenses/{id}/verify` as independent, verifiable proof.

---

## Error handling that won't bite you in production

The integration has four places where things can go wrong. Plan for each:

| Failure                              | How to recover                                                                                                |
| ------------------------------------ | ------------------------------------------------------------------------------------------------------------- |
| `401` on `POST /api/v1/licenses`     | Your API key was revoked or rotated. Alert on this immediately — it means existing users can't license music. |
| `400 Invalid licenseType`            | Usually a frontend bug. Validate on your side before calling Cambrian so you fail fast with a clear error.   |
| `429 Too Many Requests`              | You're hitting the 100 req/min limit. Add exponential backoff and consider caching catalogue search results. |
| User closes the tab mid-checkout     | The `orderId` in your database stays `initiated`. Periodically sweep and either expire or re-prompt the user. |
| Stripe webhook fires but your poll hasn't caught it yet | Use clientReferenceId + poll with verify, or implement your own webhook listener and subscribe to Cambrian events if you have partner access. |

### Caching the catalogue

The `/api/v1/tracks` and `/api/v1/tracks/{id}` endpoints are safe to cache for a few minutes at the CDN or in-process — the catalogue doesn't mutate per-request. A 60-second cache on search results typically cuts your API call volume by 80% for a tool with 100+ concurrent users.

What you **can't** cache: `POST /api/v1/licenses` (non-idempotent) and `/api/v1/licenses/{id}/verify` (freshness matters for dispute resolution).

---

## Recap

You now have:

- A 70-line `lib/cambrian.js` wrapping five endpoints with typed error handling.
- A browse/detail route pair that exposes Cambrian's catalogue through your own API.
- A purchase handler that creates an internal order row, initiates a Stripe checkout, and round-trips your order ID via `clientReferenceId`.
- A completion flow that verifies the license and stores durable proof.
- A schema for the license records your users' exports will reference.

Total new code: ~200 lines across four files. No new dependencies (all `fetch`-based). No licensing ambiguity for your users.

### Where to go next

- **[Add licensed music to AI-generated video](./add-licensed-music-to-ai-video.md)** — the video-specific variant with mood-driven track selection.
- **[Build a music-aware AI agent with MCP](./build-music-aware-mcp-agent.md)** — swap hand-rolled search logic for an LLM that calls Cambrian tools directly.
- **[Interactive API explorer](../api-explorer.html)** — try each endpoint against live data with your own key.
- **[5-minute quickstart](../quickstart.md)** — the condensed version of this tutorial.
