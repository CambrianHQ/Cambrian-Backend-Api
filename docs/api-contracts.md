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
- Payment is the existing $29 checkout (configurable `AuthorshipRecord:PriceCents`);
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

## Charts *(owned by the charts agent — not implemented here)*

```
GET  /api/charts/weekly                → { weekOf, entries: [{ rank, trackId, artist, title, delta }] }
```
