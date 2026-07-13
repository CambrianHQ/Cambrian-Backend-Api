# Shared API Contract

> **Single source of truth** for all agents working on the release-pipeline,
> earnings, charts, and notification surfaces. Do not implement an endpoint shape
> that deviates from this file — update this contract first, then implement.
> Implemented endpoints are also registered in `contracts/openapi.v1.json`.

## Track Readiness & Release Pipeline *(implemented — release-pipeline agent)*

```
GET  /api/tracks/{id}/readiness        → { score: 0-100, checks: [{ key, status: pass|warn|fail, detail }] }
POST /api/tracks/{id}/release-ready    → 202 { jobId }  |  402 if no credits
GET  /api/jobs/{id}                    → { status, stage, artifacts: [{ kind, url }] }
```

> Readiness is canonical at **`/api/tracks/{id}/readiness`** only (auth + owner-scoped).
> The legacy un-prefixed `/tracks/{id}/readiness` is a permanent **308 redirect**
> to the canonical path for stale clients — do not add new callers to it. (residue F7)

Notes:
- Readiness checks/weights: `loudness` 25 (−14 LUFS ±1), `metadata` 25,
  `aiDisclosure` 25, `cover` 15 (3000×3000 JPEG/PNG), `provenance` 10.
  pass = full weight, warn = half, fail = 0. Cached per track; invalidated on
  track/authorship/job writes (`ITrackReadinessCache`).
- `release-ready` is idempotent per (track, audio content hash): re-running
  unchanged audio returns 200 with the existing `jobId` and a warning — it never
  double-charges. Credits are the existing monthly Release Ready ledger
  (`MasteringJob.ChargedAt`); a terminal job failure automatically releases the
  credit (failed jobs leave the charged count) and reports to Sentry.
- Stages: `mastering → metadata → cover → disclosure → provenance`, persisted on
  the job (`Stage`, `StageHistoryJson`) and visible live via `GET /api/jobs/{id}`.
  Artifact kinds: `master_wav`, `master_mp3`, `disclosure` (signed URLs).

## Authorship Records & Verification *(implemented — release-pipeline agent)*

```
POST /api/releases/{id}/authorship-record  → { recordId, checkoutUrl }   (evidence refs in body)
GET  /api/authorship-records/{id}      → { status, certificate? }
GET  /verify/{recordId}                → public, no auth
```

Notes:
- `{id}` is the track id (releases are tracks at launch).
- Evidence body: `{ evidence: [{fileKey, description?}], declarations: [],
  narrative?, generator: {tool?, version?, prompts: []} }` — file refs are
  storage keys of previously-uploaded files.
- Payment is the existing $10 checkout (configurable `AuthorshipRecord:PriceCents`);
  the platform Stripe webhook issues the record on `checkout.session.completed`
  with `clientReferenceId = "{userId}:authorship:{recordId}"`.
- The issued certificate contains: canonical record JSON, its SHA-256
  (`recordHash`), a SHA-256 manifest of every evidence file, a server timestamp,
  and a signature over `cambrian-prov-v1|{recordHash}|{issuedAtUnixSeconds}`
  with the platform provenance key (**ECDSA P-256 / SHA-256**, IEEE-P1363 —
  the platform's existing signing primitive; public key published in the
  verify response). The public view exposes no PII beyond the artist name.

## Artist Monetization *(money-in implemented — release-pipeline agent)*

```
POST /api/artists/{id}/tip             → { checkoutUrl }    body: { amountCents }
POST /api/artists/{id}/subscribe      → { checkoutUrl }
PUT  /api/artists/me/subscription-price → 200                body: { priceCents | null }
GET  /api/me/earnings                  → { balance, bySource: [{ source, amount }], recent: [] }   (read agent)
```

Notes:
- `{id}` accepts an ApplicationUser id, Creator UUID, or creator username.
- Both money-in endpoints return **409** when the artist's Stripe Connect
  account is missing or payouts are disabled.
- Tips: direct charge on the artist's connected account, **application fee 0**
  at launch. Subscriptions: monthly, at the artist-set price only,
  **application_fee_percent = 15**.
- Connect webhooks arrive at `POST /webhook/stripe/connect`
  (secret: `Stripe:ConnectWebhookSecret`), which writes the earnings ledger
  (see `docs/earnings-transactions.md`). Earnings reads/aggregation are owned
  by the earnings read agent.

## Charts *(implemented — charts-and-rankings audit)*

```
GET  /api/charts/weekly                     → WeeklyChart (the running week)
GET  /api/charts/weekly/archive?limit=104   → { weeks: [{ isoWeek, weekOf, weekEnd, entries, topTrackId, topTrackTitle, topTrackArtist }] }
GET  /api/charts/weekly/archive/{isoWeek}   → WeeklyChart (one COMPLETED week, e.g. isoWeek="2026-w28") | 400 garbage key | 404 running/future/unknown week
POST /admin/charts/aggregate (Admin only)   → WeeklyChart (recompute now)
```

`WeeklyChart` shape:

```
{
  weekOf, weekEnd,            // ISO-8601 chart window (Monday 00:00 UTC → +7d, exclusive end)
  basis,                       // "weekly_plays" | "catalog_trending" (bootstrap fallback — see below)
  generatedAt,                 // ISO-8601, when this ranking was last (re)computed
  dataThrough,                 // ISO-8601, the instant play data was included through
  stale,                       // bool — true if the ranking has fallen behind its freshness target,
                                //        or a previous week is being served as a stand-in for a
                                //        not-yet-computed running week
  trackOfTheWeek: { trackId, title, artist, creatorId, coverArtUrl, description } | null,
  entries: [
    { rank, trackId, title, artist, creatorId, creatorUsername, coverArtUrl,
      deltaRank,               // vs previous week; null when new to the chart
      playsInWindow,           // qualified plays: stream sessions in the chart window, eligible track only
      lifetimePlays,           // all-time play count, frozen as of generatedAt — context only, not ranked on
      score }                  // the numeric ranking input (today: == playsInWindow)
  ]
}
```

Notes:
- All four routes are **public reads except the admin trigger**; all are
  **un-versioned** (alongside `/api/public`, `ai-discovery`) — not under
  `/api/v1`. `/charts/weekly` (no `/api` prefix) is a deliberately
  un-documented alias of `/api/charts/weekly`, excluded from the OpenAPI
  contract; do not add new callers to it.
- Ranking input is **qualified plays**: `StreamSessions` started inside the
  chart week, on an **eligible track** — `Visibility == "public"`,
  `Status == "available"` (excludes exclusive-sold, copyright-transferred,
  admin-removed, and admin-flagged tracks), not `ExclusiveSold`, and the
  creator's account not suspended. Every eligible track with a qualified play
  is a ranking candidate — there is no fixed-size candidate pool. A hard bug
  in the pre-audit implementation capped candidates at a "popular"-sorted
  200-track catalog slice (itself ordered by the dead, never-written
  `Track.TrendingScore` column); a track with real plays this week could be
  silently excluded from the chart if it fell outside that slice. Both the
  candidate-pool cap and the `TrendingScore` dependency are gone — the chart
  never reads `TrendingScore`.
- Ordering is deterministic: score desc → qualified plays desc (documented
  tie-break; today score == qualified plays) → publish time
  (`Track.CreatedAt`) desc → track id asc as the final, unconditional
  tiebreaker. Re-running a recompute over unchanged data always reproduces
  the same order.
- While a chart week has **zero** qualified plays anywhere (bootstrap —
  typically only right after a fresh deploy), the newest eligible tracks are
  shown instead (`basis: "catalog_trending"`) — never the dead
  `TrendingScore` column. Once any qualified play lands that week,
  `basis` flips to `"weekly_plays"`.
- Aggregation is **scheduled** — `WeeklyChartWorker` recomputes every
  `WeeklyChartService.RecomputeInterval` (currently 60s; freshness target:
  rankings within 60s of authoritative play data) — **and** admin-triggerable
  on demand; both call the same idempotent `AggregateAsync`, so overlapping
  triggers are harmless. Reads are served from the persisted
  `WeeklyChartSnapshots` table, not an in-process cache. `stale` in the
  response is the honest signal for "this may be older than the target" —
  the frontend should show an indicator rather than silently presenting old
  data as current.
- The archive lists **completed** weeks only, newest first; the running week
  is never archived (it lives on `/api/charts/weekly` and still changes) — a
  week only ever appears in the archive once, permanently, after it ends.
