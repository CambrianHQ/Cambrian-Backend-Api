# Cambrian Audio Rehydrate

Safe, deterministic pipeline to restore production audio playback by uploading local
audio files into the production Cloudflare R2 bucket and pointing each catalog row at
the correct object.

## Core principle

A track is playable only when **all three** hold:

1. the DB row's storage key is correct (`Track.AudioUrl` — the only field the stream path uses)
2. the R2 object exists at that key
3. `GET /stream/{trackId}/audio` returns **200/206** with an `audio/*` content-type

The tool never reports a track restored until (3) passes.

## Why Node (not a .NET tool)

R2 writes here go through `wrangler` (OAuth) and existence checks go through the
backend's own `/health/storage-probe` — so no separate R2 S3 credentials are needed,
and outbound `:5432` to Neon can be blocked (it usually is) without breaking the
dominant path. The "storage key logic" it must match is trivial and matched exactly:
**the R2 object key == the value stored in `Track.AudioUrl`.** The default action
uploads to the key the row *already* references, so most restores need no DB write.

## Setup

```bash
cd tools/audio-rehydrate
npm install
```

Credentials come from the environment (never committed):

| var | purpose |
| --- | --- |
| `REHYDRATE_ADMIN_EMAIL` / `REHYDRATE_ADMIN_PASSWORD` | admin login for `/health/*` diagnostics |
| `REHYDRATE_BACKEND` | backend base (default `https://cambrian-backend-api.onrender.com`) |
| `REHYDRATE_R2_BUCKET` | R2 bucket (default `cambrainaudio`) |
| `DATABASE_URL` | optional — Neon; used only when a *new* key must be minted |
| `CONFIRM_PRODUCTION_AUDIO_REHYDRATE` | required for `--apply` (see below) |

`wrangler` must be authenticated (`wrangler whoami`) with R2 access to the bucket.

## Usage

Scan local audio only (writes `local-audio-manifest.csv`):

```bash
node src/rehydrate.mjs --local-root <dir> [--local-root <dir> ...] --output ./reports/audio --scan-only
```

Dry run — generates every report, uploads nothing, writes nothing:

```bash
node src/rehydrate.mjs --local-root <dir> --output ./reports/audio --dry-run
```

Apply — writes to PRODUCTION. Requires the confirmation env var and only ever uploads
`exact`/`high`-confidence matches; already-playable tracks are skipped unless `--force`;
existing R2 objects are never overwritten unless `--force`:

```bash
CONFIRM_PRODUCTION_AUDIO_REHYDRATE=I_UNDERSTAND_THIS_WRITES_TO_PRODUCTION_STORAGE \
  node src/rehydrate.mjs --local-root <dir> --output ./reports/audio --apply
```

Live verification of the playback contract (read-only):

```bash
node src/verify.mjs --output ./reports/audio
```

## Reports (written to `--output`)

| file | contents |
| --- | --- |
| `local-audio-manifest.csv` | every local audio file (size, sha256, duration, guesses) |
| `production-track-manifest.csv` | every production track + current key + stream status |
| `audio-match-report.csv` | planned action per track (`upload` / `skip_already_playable` / `manual_review` / `missing_local_file` / `ambiguous_match` / `unmatched_local_file`) |
| `audio-rehydration-final-report.md` + `.csv` | totals + per-creator before/after |
| `apply-audit-log.jsonl` | (apply only) one record per attempted/changed row |
| `live-verification.csv` | (verify) per-track 200/206/404 results |

## Matching (priority order)

1. exact track id in filename → `exact`
2. local filename == current storage-key basename → `exact`
3. creator username + normalized title → `high`
4. normalized title (+ duration tolerance) → `medium`
5. otherwise → `manual_required`

Only `exact`/`high` are eligible for `--apply`. Ambiguous (multiple files match one
track at the same tier) and 0-byte files are always held for manual review.

## Idempotency / safety

- object exists + DB already points to it → no-op
- object exists but DB key wrong → DB updated only after verifying the object
- upload ok but DB update failed → logged as recoverable state
- DB update ok but stream check failed → reported as failure (not counted playable)
- never double-uploads under random keys; never overwrites without `--force`

## Tests

- Backend: `tests/Cambrian.Api.Tests/AudioRehydrationStreamTests.cs` (stream 200/206 vs clean
  404, content-type, Range pass-through, DB-key→storage-key resolution).
- Browser: `browser/audio-playback.spec.ts` (run from the frontend repo with Playwright).
