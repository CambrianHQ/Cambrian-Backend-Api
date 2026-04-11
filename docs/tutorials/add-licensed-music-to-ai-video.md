# Add licensed music to AI-generated video

**For:** Developers building AI video tools (text-to-video, image-to-video, auto-edit, social clip generators).
**Time:** 15 minutes.
**You'll build:** A server-side helper that takes a video's mood tags, picks a fitting track from Cambrian, and returns a Stripe checkout URL your user can click to license it.

---

## The problem

Your AI video tool generates a 30-second clip. The visuals are gorgeous, but the soundtrack is silence (or, worse, a stock-library loop you don't have rights to). Music licensing is the last unshipped step, and it's the one that exposes you to real legal risk — a copyright strike on a user's YouTube channel ends up on your support queue.

You need three things:

1. **Discovery** — given a mood, genre, or tempo, find a track that fits.
2. **Licensing** — let the user pay for a valid commercial license in one click.
3. **Proof** — store something the user can show if their video is ever challenged.

The Cambrian V1 API gives you all three in two HTTP calls.

---

## Prerequisites

- A Cambrian account with an API key. [Create one in 30 seconds.](../quickstart.md#1-get-an-api-key-30-seconds)
- Node 18+ (the examples use the built-in `fetch`).
- Any Stripe-compatible redirect handler on your side — you'll bounce the user to Stripe and then back to your app.

```bash
export CAMBRIAN_API_KEY="cbr_your_key_here"
```

---

## Step 1 — search for a track that fits the mood

Let's say your video's mood classifier emitted `{ mood: "uplifting", tempo: "medium", instrumental: true }`. Turn that into a Cambrian query:

```js
// search.js
const BASE = "https://api.cambrianmusic.com";

export async function findTrack({ mood, tempo, instrumental = true }) {
  const params = new URLSearchParams({
    mood,
    tempo,
    instrumental: String(instrumental),
    sort: "trending",
    limit: "10",
  });

  const res = await fetch(`${BASE}/api/v1/tracks?${params}`, {
    headers: { "X-API-Key": process.env.CAMBRIAN_API_KEY },
  });

  if (!res.ok) throw new Error(`Cambrian search failed: ${res.status}`);
  const { data } = await res.json();
  return data; // array of TrackDto
}
```

Call it:

```js
const matches = await findTrack({ mood: "uplifting", tempo: "medium" });
console.log(matches[0].title, "by", matches[0].artist);
// "Morning Commute" by Neon Halo
```

The response includes pricing for every license tier — as flat `decimal` fields in dollars, not cents — so you can surface a "from $X" badge to your user without a second round trip:

```js
const track = matches[0];
const display = `$${track.nonExclusivePrice.toFixed(2)}`;
// "$29.00"
```

Other pricing fields on every track: `price` (legacy fallback), `nonExclusivePrice`, `exclusivePrice`, `copyrightBuyoutPrice`. All are JSON numbers in dollars.

### Tuning the search for video use cases

| If your video is…   | Try these filters                                                  |
| ------------------- | ------------------------------------------------------------------ |
| A travel vlog       | `mood=uplifting&tempo=medium&instrumental=true`                    |
| An ad or promo      | `mood=energetic&sort=trending&limit=5`                             |
| A cinematic cutdown | `mood=dramatic&tempo=slow&instrumental=true`                       |
| A gaming highlight  | `genre=electronic&mood=intense&tempo=fast`                         |
| A cooking tutorial  | `mood=chill&genre=lofi&instrumental=true`                          |

The `search` parameter also accepts free text (`search=morning coffee piano`) and the API runs a fuzzy match against title, tags, and description.

### Smarter picks via the MCP server

If you want the AI to reason over multiple candidates — "find me three options, one energetic and two contemplative, all under 90 seconds" — skip the REST API and use the [MCP server](./build-music-aware-mcp-agent.md) instead. It exposes the same catalogue to Claude / Cursor / any MCP-aware LLM as a set of native tools.

---

## Step 2 — initiate a license purchase

Once the user picks a track, generate a Stripe checkout URL. This is the one endpoint that requires authentication, so your API key is essential here.

```js
// license.js
const BASE = "https://api.cambrianmusic.com";

export async function createLicenseCheckout({
  trackId,
  licenseType = "non-exclusive",
  usageType = "youtube",
  clientReferenceId,
}) {
  const res = await fetch(`${BASE}/api/v1/licenses`, {
    method: "POST",
    headers: {
      "X-API-Key": process.env.CAMBRIAN_API_KEY,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      trackId,
      licenseType,
      usageType,
      clientReferenceId, // your internal order ID — round-trips through Stripe metadata
    }),
  });

  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error(`License init failed: ${err.error || res.status}`);
  }

  return res.json(); // { success, checkoutUrl, status }
}
```

Pick `licenseType` based on what the user is doing with the video:

- **`non-exclusive`** — default. Fine for YouTube, TikTok, Reels, client deliverables. Creator can still sell the same track to others.
- **`exclusive`** — ad agencies and branded content where the buyer doesn't want the track used by anyone else again.
- **`copyright_buyout`** — full copyright transfer. Reserve for work-for-hire and in-house productions; most users will never need this.

And pick `usageType` based on the platform:

`personal` · `youtube` · `ads` · `podcast` · `game` · `film` · `social`

The `usageType` doesn't change the price — it's stamped on the issued `LicenseCertificate` so the buyer has an auditable record of what they bought it for.

### Wire it into your video generator

Here's a typical server-side route for an "add music" button in your video tool:

```js
// routes/videos/[videoId]/music.js  — e.g. Next.js or Express
import { findTrack } from "../../../lib/cambrian/search.js";
import { createLicenseCheckout } from "../../../lib/cambrian/license.js";
import { getVideo } from "../../../db/videos.js";

export async function POST(req) {
  const { videoId } = req.params;
  const video = await getVideo(videoId);

  // 1. Pick a track from Cambrian using the mood you already classified.
  const [track] = await findTrack({
    mood: video.mood,
    tempo: video.tempo,
    instrumental: true,
  });
  if (!track) return Response.json({ error: "No match" }, { status: 404 });

  // 2. Create a Cambrian license checkout tied to this video.
  const { checkoutUrl } = await createLicenseCheckout({
    trackId: track.id,
    licenseType: "non-exclusive",
    usageType: video.destination === "youtube" ? "youtube" : "social",
    clientReferenceId: `video_${videoId}`,
  });

  // 3. Redirect the user to Stripe.
  return Response.json({ checkoutUrl, track });
}
```

Your frontend opens `checkoutUrl` in a new tab (or a same-tab redirect). The buyer completes payment. Cambrian's Stripe webhook fires, issues the license, and Stripe sends the buyer back to `https://cambrianmusic.com/marketplace?view=success&trackId=…&session_id=…`.

---

## Step 3 — verify the license and attach proof to the video

After the user returns from checkout, call the verification endpoint to confirm the license is valid and stash the `licenseId` on the video record. The verify endpoint is **public** — no key needed — so you can even call it from the browser.

```js
// verify.js
const BASE = "https://api.cambrianmusic.com";

export async function verifyLicense(licenseId) {
  const res = await fetch(`${BASE}/api/v1/licenses/${licenseId}/verify`);
  if (!res.ok) return null;
  const { data } = await res.json();
  return data; // { licenseId, trackId, licenseType, usageType, buyerId, issuedAt, valid }
}
```

Store `{ licenseId, trackId, issuedAt }` alongside the video row in your database. When the user exports or publishes the video, embed the license certificate URL in the upload metadata (YouTube, TikTok, and Meta all honor custom tags here, and it dramatically shortens dispute resolution when a copyright claim fires).

---

## The one gotcha: accessing the audio file itself

The V1 public API handles discovery, licensing, and verification — but it deliberately **does not** serve the full audio file to API-key holders. That's because the audio delivery is gated by the buyer's authenticated Cambrian platform session, not by who initiated the checkout.

For an AI video tool this means:

- **Preview audio (30–60s, watermarked or limited):** available anonymously via the MCP server's `get_track_preview` tool, or via the signed preview URL in the track DTO.
- **Full-quality WAV/MP3:** the buyer downloads it by logging in at `cambrianmusic.com` after checkout. For end-users this is one extra click; for "auto-compose a video" flows where the user never touches Cambrian directly, you'll want to coordinate with us on an OAuth-on-behalf flow.

Most video tools land in the first bucket: use the preview during compose, then let the buyer finalize the download on Cambrian post-purchase. The user's video editor can then re-import the finalized file once.

We're actively working on a server-to-server delivery endpoint for authorized integrations — if this is a blocker for your use case, open an issue or email developers@cambrianmusic.com and we'll prioritize it.

---

## Recap

You now have:

1. A `findTrack()` helper that converts mood/tempo/instrumentality into a ranked list of tracks with per-tier pricing.
2. A `createLicenseCheckout()` helper that returns a Stripe URL bound to your user and your video.
3. A `verifyLicense()` helper for attaching legal proof to every video in your database.

Total new code: ~60 lines. Total new legal exposure: zero.

### Where to go next

- **[Build a music-aware AI agent with MCP](./build-music-aware-mcp-agent.md)** — have an LLM search the catalogue with free-form prompts instead of hand-wiring query params.
- **[License music programmatically in your content creation tool](./license-music-programmatically.md)** — the full end-to-end integration for non-video creator tools.
- **[Interactive API explorer](../api-explorer.html)** — try every endpoint in your browser with your own key.
