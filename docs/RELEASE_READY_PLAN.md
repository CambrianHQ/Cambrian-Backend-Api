# Release Ready — Implementation Plan (Architecture)

**Status:** Authoritative design. Implementation agents build from this document.
**Scope:** Backend (Cambrian.Api solution). Frontend is a separate Next.js repo; it integrates via the REST contract defined here.
**Last grounded against source:** 2026-05-31, branch `feat/entitlements`.

> **Architect's note — this is a plan, not code.** No migrations, endpoints, services, or UI are
> implemented here. Every schema and signature below is a specification for implementers.

---

## 0. Ethical guardrail (binds every part)

Release Ready is **honest quality + accurate AI disclosure**. It is **not** a detection-evasion tool.

Hard rules for implementers — if a task drifts across these lines, stop and escalate:

- **Never** strip, mask, alter, or "clean" anything whose purpose is to defeat AI-detection or
  provenance/watermark systems. Mastering is loudness/peak normalization + transparent limiting only.
- **Never** hide that a track is AI-generated. The AI-disclosure step is *mandatory to pass readiness*
  and is embedded verbatim into the provenance certificate.
- The "AI tool as artist name" readiness rule exists because distributors **reject** tool names as
  artist credits (accuracy/quality), **not** to conceal AI involvement. The guidance must say
  "use your real artist name," never "remove the word AI."
- The provenance certificate **discloses** model/tool and AI roles; it does not launder them.

Any requirement that reads as "make AI-generated audio undetectable" is out of scope by design.

---

## 1. Stack & conventions implementers MUST follow

| Area | Decision (existing, reuse as-is) |
| --- | --- |
| Runtime | .NET 8 / ASP.NET Core. Solution `Cambrian.sln`. |
| Layers | `Cambrian.Domain` (entities/enums), `Cambrian.Application` (DTOs, interfaces, services), `Cambrian.Persistence` (EF `CambrianDbContext`, configs, repositories, migrations), `Cambrian.Infrastructure` (storage, Stripe, ffmpeg, external), `Cambrian.Api` (controllers, middleware, startup). |
| DB | PostgreSQL via EF Core 8 / Npgsql. `CambrianDbContext : IdentityDbContext<ApplicationUser>`. Migrations auto-apply on startup (`RunMigrationsAsync`, skipped in `Testing`). |
| Migrations | New migration only — never edit an applied one. Name `yyyyMMddHHmmss_AddReleaseReady...`. Add `DbSet`s to `CambrianDbContext`, mapping via a new `*Configuration : IEntityTypeConfiguration<T>` in `Cambrian.Persistence/Configurations`. |
| Auth | ASP.NET Identity + JWT bearer. User id from `BaseController.GetRequiredUserId()` (`ClaimTypes.NameIdentifier` / `sub`). JWT claims include `role`, `tier`, `email_verified`. |
| Controllers (internal) | Inherit `BaseController`. Use envelope helpers: `OkResponse(data,msg)`, `CreatedResponse`, `MessageResponse`, `ErrorResponse`→400, `NotFoundResponse`→404, `ForbiddenResponse`→403, `ConflictResponse`→409. **No business logic in controllers** (governance `controller-layer-only-http`); delegate to Application services. **Controllers must not import `Cambrian.Domain.Entities`** (governance `dto-required`) — use DTOs. |
| Response envelope (internal) | `ApiResponse<T>` = `{ success: bool, data: T, message: string?, error: string? }`. |
| Controllers (public v1) | `[ApiController] [Route("api/v1")] [EnableRateLimiting("api_key_free")] [ServiceFilter(typeof(ApiUsageActionFilter))]`. Envelope `V1ApiResponse<T>` (`.Ok(data)` / `.Fail(msg)`). Supports `Idempotency-Key` header via `IIdempotencyStore`. |
| Errors | Validation failures auto-return `ApiResponse.Fail(joinedErrors)` (400) via `ApiBehaviorOptions`. Use the structured error **codes** defined in §3.9 for machine-readable client handling. |
| Money | Integer **cents** everywhere. No floating-point money, no rounding shortcuts (CLAUDE.md money invariant). |
| Storage | `IObjectStorage` (prod = `S3ObjectStorage` → Supabase S3 gateway). Key prefixes in use: `tracks/{creatorId}/{guid}.ext`, `covers/{creatorId}/{guid}.ext`, `images/`. Reads via `OpenReadAsync(key[, range])`; writes via `UploadAsync(stream, key, contentType)`; signed download via `GenerateDownloadUrl(key, filename)`. **Never overwrite the original asset key** — always write a new key. |
| Idempotency | Reuse `IIdempotencyStore` + `ApiIdempotencyKeys` (per `(key,userId,routeKey)`, 24h TTL) for paid run submission. |
| Background work | **None exists today.** Design a DB-backed job queue + in-process `BackgroundService` (§4). No Redis in the stack — do not add one. |
| PDF | QuestPDF (Community license, already referenced). Reuse the `LicensePdfGenerator` chrome/pattern (§9). |
| Contracts/governance | Public surface lives in `contracts/openapi.v1.json` + `contracts/endpoint-manifest.v1.json`; run `node scripts/validate-contracts.cjs` after adding endpoints. Internal `/release-ready/*` endpoints: confirm with the team whether they must be added to the OpenAPI contract (see Open Questions). |
| Frontend | Separate **Next.js / React** app (Vercel, `cambrianmusic.com`), configured via `NEXT_PUBLIC_API_URL`, `NEXT_PUBLIC_STRIPE_PUBLISHABLE_KEY`. Design system is **not** in this repo — confirm in the frontend repo (Open Question). Backend work here is the integration source of truth. |

**Do NOT rebuild** auth, catalog, streaming, creator profiles, Stripe/Pro subscription, the
three-tier licensing system, or the license-certificate system. Release Ready is additive.

---

## 2. Data model

All new tables use Postgres types matching existing conventions: `uuid`, `text`,
`character varying(n)`, `timestamp with time zone`, `integer`, `bigint`, `boolean`, `jsonb`.
All FKs to users reference `AspNetUsers.Id` (`text`) with `ON DELETE RESTRICT` (matches
`LicenseCertificates`). All "creator owns track" relationships use **`Track.Id` (uuid)** as the FK and
also store **`Track.CambrianTrackId`** (`CAMB-TRK-…`, ≤25 chars) where a human-facing/string id is
needed (certificates, mirroring `LicenseCertificate.TrackId`).

### 2.1 `TrackAiDisclosure` — AI disclosure (FREE step) · 1:1 with Track
Structured capture of the AI's role. Drives the readiness "disclosure completed" rule and is embedded
into the provenance certificate.

| Column | Type | Null | Notes |
| --- | --- | --- | --- |
| `Id` | uuid | no | PK |
| `TrackId` | uuid | no | FK → `Tracks.Id`, **UNIQUE** (1:1). Restrict. |
| `CambrianTrackId` | varchar(25) | no | denormalized for export/cert |
| `CreatorId` | text | no | FK → `AspNetUsers.Id`. Restrict. owner at disclosure time |
| `VocalsRole` | varchar(20) | no | enum string: `none` \| `ai` \| `human` \| `hybrid` |
| `InstrumentationRole` | varchar(20) | no | same enum |
| `PostProductionRole` | varchar(20) | no | same enum |
| `ModelsUsed` | jsonb | no | array of `{ name, version?, provider?, role }` (e.g. `{"name":"Suno","version":"v4","role":"instrumentation"}`) |
| `HumanContributions` | text | yes | free-text describing human creative input |
| `IsAiGenerated` | boolean | no | computed/asserted: true if any role is `ai`/`hybrid` |
| `DisclosureCompleted` | boolean | no | default false; true only when required fields set + creator confirms |
| `DdexPayload` | jsonb | yes | cached DDEX-aligned export (see §8) — regenerated on save |
| `CreatedAt` | timestamptz | no | |
| `UpdatedAt` | timestamptz | no | |

Indexes: unique `IX_TrackAiDisclosure_TrackId`; `IX_TrackAiDisclosure_CreatorId`.

### 2.2 `ReleaseReadinessReports` — latest readiness scan (FREE) · many per track (history)
Persist each scan for audit/history; the endpoint also returns it inline.

| Column | Type | Null | Notes |
| --- | --- | --- | --- |
| `Id` | uuid | no | PK |
| `TrackId` | uuid | no | FK → `Tracks.Id`. Restrict. |
| `CreatorId` | text | no | FK → `AspNetUsers.Id`. Restrict. |
| `OverallStatus` | varchar(8) | no | `pass` \| `warn` \| `fail` (worst of all checks) |
| `Checks` | jsonb | no | array of `ReadinessCheckResult` (see §6) |
| `MeasuredLufs` | double precision | yes | integrated loudness if measured |
| `MeasuredTruePeakDbtp` | double precision | yes | |
| `MeasuredDurationSeconds` | double precision | yes | from ffprobe |
| `CreatedAt` | timestamptz | no | |

Index: `IX_ReleaseReadinessReports_TrackId_CreatedAt` (TrackId, CreatedAt desc).
Optional denormalization: cache `MeasuredLufs/TruePeak/Duration` onto a small `TrackAudioAnalysis`
row keyed by TrackId so repeat scans skip ffmpeg (see Open Questions). For v1, recompute per scan.

### 2.3 `ReleaseReadyRuns` — the charging + orchestration unit (PAID)
A "run" bundles the paid steps the creator requested. **Exactly one credit is charged per run** that
includes ≥1 paid step (Pro = free). This is the entitlement boundary.

| Column | Type | Null | Notes |
| --- | --- | --- | --- |
| `Id` | uuid | no | PK |
| `TrackId` | uuid | no | FK → `Tracks.Id`. Restrict. |
| `CreatorId` | text | no | FK → `AspNetUsers.Id`. Restrict. |
| `IncludeMaster` | boolean | no | step requested |
| `IncludeCover` | boolean | no | step requested |
| `IncludeCertificate` | boolean | no | step requested |
| `Status` | varchar(12) | no | `queued` \| `processing` \| `done` \| `failed` \| `partial` |
| `ChargeSource` | varchar(8) | no | `pro` \| `credit` (audit of how it was paid) |
| `CreditLedgerId` | uuid | yes | FK → `ReleaseCreditLedger.Id` (the debit row); null when `pro` |
| `MasterStatus` | varchar(12) | yes | per-step: `queued`/`processing`/`done`/`failed`/`skipped` |
| `CoverStatus` | varchar(12) | yes | same |
| `CertificateStatus` | varchar(12) | yes | same |
| `MasterResultId` | uuid | yes | FK → `MasteringResults.Id` |
| `CoverResultId` | uuid | yes | FK → `CoverArtResults.Id` |
| `CertificateId` | uuid | yes | FK → `ProvenanceCertificates.Id` |
| `Error` | text | yes | top-level failure message |
| `Options` | jsonb | yes | step options (target LUFS override, adopt-as-catalog flag, attestations for cert, etc.) |
| `IdempotencyKey` | varchar(128) | yes | client-supplied; unique per `(CreatorId, key)` when present |
| `CreatedAt` | timestamptz | no | |
| `StartedAt` | timestamptz | yes | worker claim time |
| `CompletedAt` | timestamptz | yes | |

Indexes: `IX_ReleaseReadyRuns_Status` (worker poll), `IX_ReleaseReadyRuns_TrackId`,
`IX_ReleaseReadyRuns_CreatorId`, unique partial `UX_ReleaseReadyRuns_Creator_Idem` on
`(CreatorId, IdempotencyKey)` where `IdempotencyKey` not null.

### 2.4 `MasteringResults` — output of the master step (PAID)
| Column | Type | Null | Notes |
| --- | --- | --- | --- |
| `Id` | uuid | no | PK |
| `RunId` | uuid | no | FK → `ReleaseReadyRuns.Id`. Restrict. |
| `TrackId` | uuid | no | FK → `Tracks.Id`. |
| `SourceAudioKey` | text | no | the original key, **read-only, never modified** |
| `MasteredAudioKey` | text | yes | new asset key, e.g. `release-ready/master/{trackId}/{guid}.mp3` |
| `TargetLufs` | double precision | no | default `-14.0` |
| `TargetTruePeakDbtp` | double precision | no | default `-1.0` |
| `InputLufs` | double precision | yes | measured pre |
| `OutputLufs` | double precision | yes | measured post |
| `OutputTruePeakDbtp` | double precision | yes | |
| `OutputFormat` | varchar(8) | no | `mp3` (320kbps) for v1; WAV optional later |
| `Status` | varchar(12) | no | `queued`/`processing`/`done`/`failed` |
| `Error` | text | yes | |
| `CreatedAt` / `CompletedAt` | timestamptz | yes | |

> Mastering writes a **new** key and stores it here. The catalog `Track.AudioUrl` is **not** changed
> unless the creator explicitly adopts the master (see §3.5, `adoptAsCatalogAudio`), which is a
> separate, deliberate write.

### 2.5 `CoverArtResults` — output of the cover step (PAID)
| Column | Type | Null | Notes |
| --- | --- | --- | --- |
| `Id` | uuid | no | PK |
| `RunId` | uuid | no | FK → `ReleaseReadyRuns.Id`. Restrict. |
| `TrackId` | uuid | no | FK → `Tracks.Id`. |
| `SourceCoverKey` | text | yes | original cover key (may be null if none) |
| `OutputCoverKey` | text | yes | new asset, e.g. `release-ready/cover/{trackId}/{guid}.jpg` |
| `Mode` | varchar(10) | no | `validate` \| `generate` (resize/pad) |
| `Width` / `Height` | integer | yes | output dims (3000×3000) |
| `Format` | varchar(8) | yes | `jpeg` |
| `ColorSpace` | varchar(8) | yes | `rgb` |
| `Status` | varchar(12) | no | |
| `Error` | text | yes | |
| `CreatedAt` / `CompletedAt` | timestamptz | yes | |

### 2.6 `ProvenanceCertificates` — extends the certificate system (PAID)
**Why a new table, not a `LicenseCertificate` row:** `LicenseCertificate` is buyer-facing and requires
a non-null `PurchaseId` FK (a license is issued against a *purchase*). A provenance certificate is
**creator-facing**, issued against the creator's *own track with no purchase*. Reusing
`LicenseCertificate` would force a fake purchase. Instead we **reuse the certificate *patterns***:
Guid id, QuestPDF generation, on-the-fly/stored PDF, the public `verify/{id}` endpoint shape, and the
layered service/repository structure. Refactor the shared PDF header/footer/table chrome out of
`LicensePdfGenerator` into a small reusable helper (`CertificatePdfTheme`) that both generators use —
this is the "extend, don't duplicate" requirement.

| Column | Type | Null | Notes |
| --- | --- | --- | --- |
| `Id` | uuid | no | PK. Certificate id (string form exposed to clients, like `LicenseCertificate`). |
| `TrackId` | uuid | no | FK → `Tracks.Id`. Restrict. |
| `CambrianTrackId` | varchar(25) | no | human id on the cert |
| `CreatorId` | text | no | FK → `AspNetUsers.Id`. Restrict. |
| `RunId` | uuid | yes | FK → `ReleaseReadyRuns.Id` (the paid run that issued it) |
| `ModelsUsed` | jsonb | no | snapshot from disclosure (model/tool + version + provider + role) |
| `CommercialRightsBasis` | varchar(40) | no | e.g. `creator-paid-plan-attestation` |
| `CommercialRightsAttestedPlan` | varchar(20) | yes | the plan the creator held at attestation (`pro`/`free`) |
| `SamplesStemsSource` | jsonb | yes | array of `{ description, source, licensed: bool }` |
| `OwnershipAttestation` | text | no | the creator's signed ownership statement (captured at issue) |
| `DisclosureSnapshot` | jsonb | no | full copy of `TrackAiDisclosure` at issue time (immutable) |
| `DisclosureId` | uuid | yes | FK → `TrackAiDisclosure.Id` (provenance link) |
| `PdfStorageKey` | text | yes | stored PDF key, e.g. `release-ready/cert/{id}.pdf` (recommended: store for immutable verification) |
| `IssuedAt` | timestamptz | no | |
| `RevokedAt` | timestamptz | yes | allow revoke if attestation later found false |

Indexes: `IX_ProvenanceCertificates_TrackId`, `IX_ProvenanceCertificates_CreatorId`.

### 2.7 Credit ledger (NEW — no credit system exists today)
There is **no** existing consumable-credit concept. `WalletTransaction` / `WalletBalanceCents` is the
**creator-earnings** ledger (money owed to creators, payouts) — do **not** reuse it for feature
credits. The `Entitlement` table is **resource-access** (stream/download/license of a track) — also
not a consumable. Design a minimal, race-safe credit system:

**`ReleaseCreditAccounts`** — one row per user; holds the authoritative balance for atomic debit.
| Column | Type | Null | Notes |
| --- | --- | --- | --- |
| `UserId` | text | no | **PK**, FK → `AspNetUsers.Id`. Restrict. |
| `BalanceCredits` | integer | no | default 0; never negative (enforced by debit SQL) |
| `UpdatedAt` | timestamptz | no | |

**`ReleaseCreditLedger`** — append-only audit of every grant/debit/refund.
| Column | Type | Null | Notes |
| --- | --- | --- | --- |
| `Id` | uuid | no | PK |
| `UserId` | text | no | FK → `AspNetUsers.Id`. Restrict. |
| `Delta` | integer | no | `+N` (purchase/promo) or `-1` (run debit) or `+1` (refund) |
| `Reason` | varchar(30) | no | `stripe_topup` \| `run_debit` \| `run_refund` \| `admin_grant` |
| `RunId` | uuid | yes | FK → `ReleaseReadyRuns.Id` for debit/refund rows |
| `StripeEventId` | varchar(255) | yes | dedupe top-ups (mirrors `StripeWebhookEvents.EventId`) |
| `BalanceAfter` | integer | no | snapshot for audit |
| `CreatedAt` | timestamptz | no | |

Indexes: `IX_ReleaseCreditLedger_UserId_CreatedAt`; unique `UX_ReleaseCreditLedger_StripeEventId`
(where not null) so a replayed webhook can't double-credit.

**Debit must be race-safe** (CLAUDE.md concurrency invariant): use a single conditional SQL update
`UPDATE "ReleaseCreditAccounts" SET "BalanceCredits" = "BalanceCredits" - 1, "UpdatedAt" = now()
WHERE "UserId" = @u AND "BalanceCredits" >= 1 RETURNING "BalanceCredits"` (via
`ExecuteSqlInterpolatedAsync`/raw). 0 rows affected ⇒ insufficient credits. Write the ledger row in
the same transaction. **Do not** read-then-write with tracked entities for the debit.

### 2.8 Relationship summary
- `Track` 1—1 `TrackAiDisclosure`; 1—* `ReleaseReadinessReports`; 1—* `ReleaseReadyRuns`;
  1—* `ProvenanceCertificates`.
- `ReleaseReadyRun` 1—0..1 each of `MasteringResults`, `CoverArtResults`, `ProvenanceCertificates`;
  1—0..1 `ReleaseCreditLedger` (the debit).
- `ApplicationUser` 1—1 `ReleaseCreditAccount`; 1—* `ReleaseCreditLedger`.
- No cascade deletes anywhere (Restrict), consistent with `LicenseCertificates`.

---

## 3. API contract (shared source of truth)

**Base prefix (internal/creator-facing):** `/release-ready`. Internal controllers, `BaseController`
envelope (`ApiResponse<T>`). All require `[Authorize]`. All track-scoped routes additionally require
**ownership** (`Track.CreatorId == userId` OR caller is `Admin`); a non-owner gets **404**
(`NotFoundResponse`) to avoid leaking track existence (matches existing stream/visibility pattern).

Entitlement gates are enforced **server-side in the Application service**, not in the controller
(§5). Notation: **FREE** = ownership only; **PAID** = Pro-or-credit.

### 3.1 Readiness check — FREE
**`POST /release-ready/tracks/{trackId}/readiness`** — run a fresh scan.
- Auth: Bearer. Gate: FREE (owner).
- Path: `trackId` = Track UUID.
- Request body: none (optional `{ "recomputeAudio": true }` to force re-measuring loudness).
- 200 `ApiResponse<ReadinessReport>`:
  ```json
  { "success": true, "data": {
    "trackId": "uuid", "overallStatus": "warn",
    "measured": { "lufs": -19.3, "truePeakDbtp": -0.4, "durationSeconds": 47.2 },
    "checks": [
      { "id": "loudness", "status": "fail", "title": "Loudness not normalized",
        "detail": "Integrated loudness is -19.3 LUFS (target ~-14).",
        "guidance": "Run Mastering to normalize to -14 LUFS / -1 dBTP." },
      { "id": "true_peak", "status": "warn", ... },
      { "id": "metadata_artist", "status": "pass", ... },
      { "id": "metadata_credits", "status": "fail", ... },
      { "id": "metadata_title", "status": "warn", ... },
      { "id": "duplicate", "status": "pass", ... },
      { "id": "duration", "status": "fail", ... },
      { "id": "ai_disclosure", "status": "fail", ... }
    ],
    "generatedAt": "2026-05-31T12:00:00Z" } }
  ```
- Errors: 401 unauth; 404 track not found / not owner; 400 `audio_unreadable` if storage read fails
  (returns report with `loudness`/`duration` checks marked `warn` + detail, **not** a hard 500).

**`GET /release-ready/tracks/{trackId}/readiness`** — latest persisted report.
- FREE. 200 `ApiResponse<ReadinessReport>`; 404 if track not found/owner; returns `data: null` (200)
  if no scan run yet, or 404 `no_report` — **decision: 200 with `data:null`** so the UI can prompt a scan.

### 3.2 AI disclosure — FREE
**`GET /release-ready/tracks/{trackId}/disclosure`**
- FREE. 200 `ApiResponse<DisclosureDto>` (returns empty/default shape with `disclosureCompleted:false`
  when none exists). 404 track not found/owner.

**`PUT /release-ready/tracks/{trackId}/disclosure`** — upsert.
- FREE. Request body `DisclosureUpsertRequest`:
  ```json
  { "vocalsRole": "ai", "instrumentationRole": "ai", "postProductionRole": "human",
    "modelsUsed": [ { "name": "Suno", "version": "v4", "provider": "Suno Inc.", "role": "instrumentation" } ],
    "humanContributions": "Wrote lyrics, arranged structure, mixed.",
    "confirmAccurate": true }
  ```
- Validation: each role ∈ `{none,ai,human,hybrid}`; `modelsUsed[].name` required when any role is
  `ai`/`hybrid`; `confirmAccurate` must be `true` to set `disclosureCompleted=true`.
- 200 `ApiResponse<DisclosureDto>` (includes computed `isAiGenerated`, `disclosureCompleted`, and the
  `ddexPayload`). Errors: 400 `validation` (with field detail); 404 track/owner.

### 3.3 Credits & Pro status — FREE (read) / PAID top-up
**`GET /release-ready/credits`**
- Auth. 200 `ApiResponse<CreditStatusDto>`: `{ "isPro": true, "balanceCredits": 0, "unlimited": true }`
  (`unlimited:true` when Pro). 401 unauth.

**`GET /release-ready/credits/transactions?take=50`**
- Auth. 200 `ApiResponse<CreditLedgerEntryDto[]>` (most-recent first).

**`POST /release-ready/credits/checkout`** — buy a credit pack via existing Stripe Checkout.
- Auth. Request `{ "pack": "single" | "pack5" | "pack20" }`.
- 200 `ApiResponse<{ checkoutUrl: string }>` — Stripe-hosted URL (reuses `IPaymentGateway`,
  fulfilled by the webhook in §10).
- Errors: 400 `invalid_pack`; 401. Idempotency-Key honored.

### 3.4 Create a paid run — PAID (the credit boundary)
**`POST /release-ready/tracks/{trackId}/runs`**
- Auth. Gate: **PAID** — Pro → free; else atomically debit 1 credit.
- Headers: optional `Idempotency-Key`.
- Request `RunCreateRequest`:
  ```json
  { "master": true, "cover": true, "certificate": true,
    "options": {
      "targetLufs": -14.0, "adoptMasterAsCatalogAudio": false,
      "coverMode": "generate",
      "certificate": {
        "ownershipAttestation": "I own or have full rights to release this track.",
        "samplesStems": [ { "description": "Drum loop", "source": "Self-recorded", "licensed": true } ],
        "confirmCommercialRights": true
      }
    } }
  ```
- Rules:
  - At least one of `master`/`cover`/`certificate` must be `true` → else 400 `no_paid_steps`.
  - `certificate:true` requires `disclosureCompleted == true` for the track → else 409
    `disclosure_required` (disclosure is free; cannot certify provenance without it).
  - `certificate:true` requires `options.certificate.confirmCommercialRights == true` and a non-empty
    `ownershipAttestation` → else 400 `attestation_required`.
- On success: charge resolved (Pro or 1 credit), `ReleaseReadyRun` created `status=queued`, async work
  enqueued (§4). 202 (use `CreatedResponse` → 201 is acceptable; **decision: 201 Created**)
  `ApiResponse<RunDto>` with `status:"queued"` and per-step statuses.
- Errors:
  - 401 unauth; 404 track not found/owner.
  - 403 `insufficient_credits` (not Pro, balance 0) — body includes
    `{ error, code:"insufficient_credits", hint:"Buy credits or upgrade to Pro" }`.
  - 400 `no_paid_steps` / `attestation_required`; 409 `disclosure_required`.
  - Idempotent replay → returns the original `RunDto` (no second charge).

**`GET /release-ready/runs/{runId}`** — status polling.
- Auth + owner. 200 `ApiResponse<RunDto>`:
  ```json
  { "success": true, "data": {
    "id": "uuid", "trackId": "uuid", "status": "processing", "chargeSource": "credit",
    "steps": {
      "master": { "status": "done", "downloadUrl": "https://.../signed", "inputLufs": -19.3, "outputLufs": -14.0, "outputTruePeakDbtp": -1.0 },
      "cover":  { "status": "processing" },
      "certificate": { "status": "queued", "certificateId": null }
    },
    "error": null, "createdAt": "...", "completedAt": null } }
  ```
- `downloadUrl` fields are short-lived signed URLs (or relative proxy paths) populated when a step is
  `done`. 404 if run not found/owner.

**`GET /release-ready/tracks/{trackId}/runs?take=20`** — list runs for a track. Auth+owner. 200
`ApiResponse<RunSummaryDto[]>`.

### 3.5 Mastering output — PAID (results of a run)
**`GET /release-ready/runs/{runId}/master/download`**
- Auth+owner. 302 redirect to signed URL **or** streamed bytes (match `DownloadController` behavior:
  signed URL for S3, proxy for local). 404 if no mastered asset / not done. Original is never touched.
- Adoption (optional, deliberate): controlled by `options.adoptMasterAsCatalogAudio` at run creation —
  when true and mastering succeeds, the worker sets `Track.AudioUrl` to the new key (a separate write;
  the prior key is retained in `MasteringResults.SourceAudioKey`). Default false.

### 3.6 Cover output — PAID
**`GET /release-ready/runs/{runId}/cover/download`** — same shape as master download.

### 3.7 Provenance certificate — PAID (issued as a run step)
Issued by the run when `certificate:true`. Retrieval/verification endpoints:

**`GET /release-ready/certificates/{certificateId}`** — Auth+owner. 200 `ApiResponse<ProvenanceCertDto>`.
404 if not found/owner.

**`GET /release-ready/certificates/{certificateId}/pdf`** — Auth+owner. `File(application/pdf)`.
Serves the stored PDF (`PdfStorageKey`) or renders on the fly if not stored. Filename
`provenance-{trackTitleSanitized}-{id[..8]}.pdf` (reuse `FilenameHelper.SanitizeFilename`). 404/owner.

**`GET /release-ready/certificates/verify/{certificateId}`** — **public** (`[AllowAnonymous]`).
- 200 `ApiResponse<ProvenanceVerifyDto>` — non-PII subset:
  ```json
  { "valid": true, "certificateId": "uuid", "trackTitle": "…", "cambrianTrackId": "CAMB-TRK-…",
    "creatorDisplayName": "…", "isAiGenerated": true,
    "aiRoles": { "vocals": "ai", "instrumentation": "ai", "postProduction": "human" },
    "modelsUsed": [ { "name": "Suno", "role": "instrumentation" } ],
    "issuedAt": "…", "revoked": false }
  ```
- 404 if not found. Mirrors `GET /licenses/verify/{id}`.

### 3.8 Public v1 mirror (optional, for the documented API) — PAID/Public
For parity with `LicensesV1Controller`, expose verification publicly under the versioned API:
**`GET /api/v1/provenance/{id}/verify`** `[AllowAnonymous]` → `V1ApiResponse<ProvenanceVerifyResponse>`
(same data as §3.7 verify). Add to `contracts/openapi.v1.json`. (Run creation stays on the internal
surface for v1 scope; revisit if third parties need to trigger runs.)

### 3.9 Error code catalog (machine-readable `code` on `ApiResponse.error`)
| HTTP | code | meaning |
| --- | --- | --- |
| 400 | `validation` | request body invalid (field detail in message) |
| 400 | `no_paid_steps` | run had no paid step selected |
| 400 | `attestation_required` | certificate step missing ownership/commercial-rights attestation |
| 400 | `invalid_pack` | unknown credit pack |
| 400 | `audio_unreadable` | storage read failed during scan |
| 401 | `unauthenticated` | missing/invalid token |
| 403 | `insufficient_credits` | not Pro and balance 0 |
| 404 | `not_found` | track/run/cert absent or not owned |
| 409 | `disclosure_required` | certificate requested before AI disclosure completed |
| 409 | `run_in_progress` | (optional) a run for this track is already active |

> The `code` rides inside the existing envelope. If `ApiResponse` has no `code` field today,
> implementers may either (a) add an optional `code` to the envelope (low-risk, additive) or
> (b) encode it as a documented prefix in `error`. **Decision: add optional `code` to `ApiResponse`**
> (additive, backward-compatible) — confirm in review.

---

## 4. Background-job design

**Why async:** mastering (ffmpeg, seconds–minutes) and cover generation (image decode/resize/encode)
must not block request threads. Readiness, disclosure, certificate-record creation, and credit
accounting are synchronous. Certificate **PDF render** is fast but depends on the run's other steps,
so it runs as the final worker step.

**Queue:** DB-backed via `ReleaseReadyRuns` (`Status='queued'`) — no external broker (no Redis).
**Worker:** a single `BackgroundService` (`ReleaseReadyWorker`) registered with
`builder.Services.AddHostedService<ReleaseReadyWorker>()` (first hosted service in the app; none exist
today). It runs **in-process** in the API (current Render topology = one instance per env).

Loop:
1. Poll every N seconds (e.g. 3s) **and** wake on an in-process signal (a `Channel<Guid>` the run
   endpoint writes to) for low latency.
2. **Claim** a job race-safely: `UPDATE "ReleaseReadyRuns" SET "Status"='processing', "StartedAt"=now()
   WHERE "Id"=@id AND "Status"='queued'` (0 rows ⇒ someone else took it). This keeps it correct even
   if the app is later scaled to >1 instance.
3. Create a DI scope per job (`IServiceScopeFactory`) for scoped `DbContext`/repos.
4. Execute requested steps in order: **master → cover → certificate** (certificate last so it can
   reference outputs/measurements). Each step writes its `*Results` row + per-step status.
5. Set run `Status`: `done` (all requested succeeded), `partial` (some succeeded), or `failed` (all
   failed). On total failure with `ChargeSource='credit'`, **refund the credit** (ledger `+1`,
   reason `run_refund`) in the same transaction and set `CreditLedgerId` accordingly.
6. Bound concurrency: a `SemaphoreSlim` (e.g. 1–2) caps simultaneous ffmpeg processes (small Render
   instance). Per-job timeout (e.g. 8 min) kills runaway ffmpeg via `Process.Kill(entireProcessTree:true)`.

**Status model exposed:** `queued → processing → done|failed|partial` at the run level; each step has
`queued|processing|done|failed|skipped`. Exposed via `GET /release-ready/runs/{runId}` (§3.4). No
websockets — the frontend polls (e.g. every 2–3s) until terminal.

**Crash recovery:** on worker startup, reset orphaned `processing` rows older than the job timeout
back to `queued` (or mark `failed` + refund) so a deploy mid-job doesn't strand a run.

**ffmpeg invocation (Infrastructure service `IAudioMasteringService`):**
- Download source from `IObjectStorage.OpenReadAsync(sourceKey)` to a temp file.
- Two-pass loudnorm (measure, then apply) — see §7.
- Probe duration/loudness with `ffprobe`/`ffmpeg -af ebur128` (also used by the readiness loudness check).
- Upload result to the new key; delete temp files in a `finally`.
- ffmpeg must be installed in the runtime image (§10, Dockerfile change — infra task).

**Image (Infrastructure service `ICoverArtService`):** SixLabors.ImageSharp (pure-managed, no native
deps) for decode/validate/resize/recolor/encode. No ImageMagick/SkiaSharp needed.

---

## 5. Entitlement logic (exact rules + enforcement points)

**Plan resolution** (`IReleaseEntitlementService.ResolveAsync(userId)`):
- `IsPro` ⇔ `user.CreatorTier == CreatorTier.Pro || string.Equals(user.Tier, "pro", OrdinalIgnoreCase)`
  — **identical** to `CapabilityResolver.isPro`, so Release Ready and capabilities agree. (Subscription
  lapse already resets `CreatorTier`/`Tier` via `SubscriptionService`; no extra active-check needed.)
- `BalanceCredits` from `ReleaseCreditAccounts` (0 if no row).

**Rules:**
| Operation | Free user | Pro user |
| --- | --- | --- |
| Readiness check | free, unlimited | free, unlimited |
| AI disclosure (GET/PUT) | free, unlimited | free, unlimited |
| Credits read / top-up checkout | allowed | allowed (Pro can still buy, harmless) |
| Run with ≥1 paid step | **1 credit per run** (atomic debit); 403 `insufficient_credits` if balance 0 | free, unlimited (`ChargeSource='pro'`) |

- **One credit per run**, regardless of how many paid steps (master+cover+certificate in one run = 1).
- **Charge happens once, at run creation**, *before* enqueue, inside a DB transaction with the run
  insert. Order: resolve plan → if Pro, insert run (`pro`); else atomic debit (§2.7) → on success
  insert run + ledger debit (`credit`) → else 403. Idempotent replays do not re-charge.
- **Refund on total failure** (worker, §4 step 5).

**Enforcement location:** exclusively in `ReleaseReadyRunService` (Application layer). Controllers only
authenticate + check ownership + translate exceptions to envelopes. No gate logic in controllers
(governance). Ownership check: a shared `IReleaseTrackGuard.EnsureOwnerAsync(trackId, userId)` used by
every track-scoped endpoint.

> No new authorization *policy* is added; the gate is plan/credit state, not a static capability.
> (Capabilities remain role/tier-derived in `CapabilityResolver`; Release Ready does not extend them.)

---

## 6. Readiness-check rules

Each check yields `{ id, status: pass|warn|fail, title, detail, guidance }`. **Overall = worst**
(`fail` > `warn` > `pass`). All thresholds below are the **proposed defaults** (centralize in a
`ReleaseReadinessOptions` config object so they're tunable without redeploy logic changes); values
flagged in Open Questions.

| id | Trigger / how measured | pass | warn | fail | Guidance (on warn/fail) |
| --- | --- | --- | --- | --- | --- |
| `loudness` | Integrated LUFS via `ffmpeg -af ebur128` (or loudnorm pass-1 JSON). | within −14 ±1.5 LUFS | −16…−12 outside pass band, or −20…−16 / −12…−8 | < −20 or > −8 LUFS | "Run Mastering to normalize to ~−14 LUFS." |
| `true_peak` | True-peak dBTP from same analysis. | ≤ −1.0 dBTP | −1.0 … 0.0 | > 0.0 (clipping) | "Reduce peaks below −1 dBTP (Mastering does this)." |
| `metadata_artist` | Artist/`Creator.DisplayName` vs a denylist of known AI tool names (Suno, Udio, MusicGEN, Riffusion, "AI", …). | not a tool name | contains tool name as a token | equals a tool name | "Distributors reject tool names as artist. Use your real artist name." *(accuracy, not concealment)* |
| `metadata_credits` | Presence of artist + at least basic credits (artist/display name set). | present | — | blank/placeholder | "Add your artist name and songwriting credits." |
| `metadata_title` | `Track.Title` quality: length, placeholder patterns (`untitled`, `track \d+`, `beat`, `demo`, digits-only, < 2 chars). | descriptive | matches a vague pattern | empty | "Use a specific, descriptive title." |
| `duplicate` | Near-duplicate of the creator's other tracks: (a) exact stored audio object-hash match ⇒ fail; (b) very-similar normalized title + matching duration (±2s) ⇒ warn. v1 = metadata+hash heuristic (no acoustic fingerprint). | none | title+duration match | exact content match | "This looks like an existing track — release one canonical version." |
| `duration` | Track length in seconds (ffprobe; or parse `Track.Duration`). | ≥ 60s | 30–60s | < 30s | "Tracks under ~60s are often rejected/flagged; extend or reconsider the release." |
| `ai_disclosure` | `TrackAiDisclosure.DisclosureCompleted`. | true | — | false/missing | "Complete your AI disclosure (free) before release." |

Notes:
- `loudness`/`true_peak`/`duration` require reading the audio; if storage read fails, mark those three
  `warn` with `detail:"could not analyze audio"` and surface `code:audio_unreadable` — never 500.
- The duplicate check scopes to `Track.CreatorId == this creator`, excludes the track itself, and only
  considers `Visibility != hidden`/non-deleted tracks.
- The `ebur128` analysis is the **same** routine the mastering pass uses — share it
  (`IAudioAnalysisService.MeasureAsync(stream) → { Lufs, TruePeak, DurationSeconds }`).

---

## 7. Mastering spec

- **Target:** integrated loudness **−14 LUFS**, true peak **−1.0 dBTP**, loudness range ~**11 LU**
  (streaming-friendly; tunable).
- **Method:** ffmpeg **two-pass `loudnorm`** (transparent, EBU R128):
  1. Pass 1 (analysis): `ffmpeg -i in -af loudnorm=I=-14:TP=-1.0:LRA=11:print_format=json -f null -` →
     parse measured `input_i`, `input_tp`, `input_lra`, `input_thresh`, `target_offset`.
  2. Pass 2 (apply): `ffmpeg -i in -af loudnorm=I=-14:TP=-1.0:LRA=11:measured_I=…:measured_TP=…:measured_LRA=…:measured_thresh=…:offset=…:linear=true -ar 44100 -b:a 320k out.mp3`.
  - `linear=true` keeps it transparent (no dynamic pumping) when possible; ffmpeg falls back to
     dynamic only if linear can't hit target.
- **Light transparent limiting only** — loudnorm's TP limiter handles the −1 dBTP ceiling. **Do not**
  add aggressive multiband/clipper/exciter chains. No fingerprint/watermark manipulation (ethical line).
- **Output:** MP3 320 kbps, 44.1 kHz, stereo (v1). WAV 24-bit optional later. Stored at a **new** key
  `release-ready/master/{trackId}/{guid}.mp3`. Record in/out LUFS + out TP in `MasteringResults`.
- **Never overwrite** `SourceAudioKey`. Catalog `Track.AudioUrl` only changes if
  `adoptMasterAsCatalogAudio` was requested (deliberate, reversible — original key retained).

---

## 8. Cover-art spec

- **DSP target:** **3000×3000 px**, **square (1:1)**, **RGB** (sRGB; reject/convert CMYK), format
  **JPEG** (quality ~90) or PNG; ≤ ~10 MB output. (Mirrors Spotify/Apple/DistroKit requirements.)
- **`validate` mode (cheap):** decode with ImageSharp, report compliant or list violations (too small,
  non-square, wrong color space, unsupported format). Does **not** modify.
- **`generate` mode:** produce a compliant asset: if ≥3000² and square, re-encode to sRGB JPEG; if
  smaller, high-quality Lanczos upscale to 3000² (never beyond a sane upscale cap — warn if source <
  1000²); if non-square, **pad** (letterbox with sampled background) rather than crop by default
  (crop is destructive). Convert CMYK→sRGB. Strip nothing required by DSPs except fix color profile.
- **Output:** new key `release-ready/cover/{trackId}/{guid}.jpg`; record dims/format/colorspace in
  `CoverArtResults`. Original `Track.CoverArtUrl` unchanged unless an explicit adopt flag is added
  later (out of v1 scope).
- **Library:** SixLabors.ImageSharp (managed, no native deps; confirm license — it's Apache-2.0 for
  current versions / Six Labors Split License for newer — see Open Questions).

---

## 9. Integration notes (reuse, don't duplicate)

**License-certificate system → provenance certificate:**
- Reuse the **QuestPDF** approach. Refactor shared chrome (header logo, footer, two-column detail
  table, section/bullet helpers, legal-notice block) out of `LicensePdfGenerator` into
  `Cambrian.Api/Tools/CertificatePdfTheme.cs`; add `ProvenancePdfGenerator` that composes it. Leave
  `LicensePdfGenerator` behavior identical (regression-test it).
- Reuse the **certificate id = Guid** convention and the **public `verify/{id}`** endpoint shape.
- Reuse `FilenameHelper.SanitizeFilename` for the download filename.
- New `IProvenanceCertificateService` + `IProvenanceCertificateRepository` mirror `ILicenseService` /
  `ILicenseCertificateRepository` layering. Do **not** add a `PurchaseId` to provenance.

**Stripe / Pro:**
- Pro detection reuses the same condition as `CapabilityResolver` (§5). Do not introduce a parallel
  notion of "Pro."
- Credit-pack purchase reuses `IPaymentGateway.CreateCheckoutSessionAsync` (one-off Checkout Session)
  with `clientReferenceId`/metadata `{ type:"release_credits", userId, credits:N }`. Fulfillment in
  the **protected** `StripeWebhookService` (see §10) — adds a new metadata branch; preserves
  `StripeWebhookEvents.EventId` idempotency. **Get explicit approval before editing
  `StripeWebhookService` / payment code** (CLAUDE.md protected systems).

**Catalog / Track:**
- Read `Track` via existing `ITrackRepository.GetByIdAsync(Guid)`. Use `Track.Id` (uuid) for FKs,
  `Track.CambrianTrackId` for human ids, `Track.AudioUrl` / `Track.CoverArtUrl` as source asset keys,
  `Track.Duration` as a fallback when ffprobe isn't run. Ownership = `Track.CreatorId == userId`.

**Storage:**
- All new assets via `IObjectStorage.UploadAsync(stream, key, contentType)` with the
  `release-ready/...` prefixes above; downloads via `GenerateDownloadUrl` (signed) or the
  local-storage proxy fallback exactly like `DownloadController`. PDFs: store at
  `release-ready/cert/{id}.pdf` for immutable verification (recommended) or render on the fly
  (matches license PDFs). **Decision: store provenance PDFs** (verification must be reproducible).

**Idempotency:** reuse `IIdempotencyStore` for `POST /runs` and `POST /credits/checkout`.

---

## 10. Infrastructure changes (non-feature, infra tasks)

1. **Dockerfile:** add ffmpeg to the runtime stage:
   `RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*`
   (before the non-root `USER` switch). Verify `ffmpeg`/`ffprobe` on `PATH`. Note image-size increase.
2. **NuGet:** add `SixLabors.ImageSharp` to `Cambrian.Infrastructure` (confirm license terms).
3. **DI (`Program.cs`):** register `IReleaseEntitlementService`, `IReleaseTrackGuard`,
   `IReleaseReadinessService`, `IAudioAnalysisService`, `IAudioMasteringService`, `ICoverArtService`,
   `IReleaseReadyRunService`, `IProvenanceCertificateService`, credit ledger services, repositories,
   and `AddHostedService<ReleaseReadyWorker>()` (+ the wake `Channel`).
4. **Stripe webhook (PROTECTED — needs approval):** add a `release_credits` fulfillment branch keyed on
   session metadata; credit the ledger atomically + write `StripeEventId` for dedupe.
5. **Render:** evaluate whether the in-process worker is acceptable on the current instance size or a
   separate worker service is needed (Open Questions). No new env vars required for v1 beyond an
   optional `ReleaseReady__*` options block (thresholds, target LUFS, credit-pack price ids).

---

## 11. Build order / task breakdown

Designed to split across two implementation agents (**A = data/jobs/audio**, **B = certificate/
disclosure/credits/API**). Shared contracts (§2, §3) are the sync point.

**Phase 0 — foundation (either agent, do first)**
0.1 Migration: all tables in §2 (one migration) + `DbSet`s + EF configs. *(no business logic)*
0.2 DTOs (Application): `ReadinessReport`, `DisclosureDto`/`DisclosureUpsertRequest`, `RunDto`/
    `RunCreateRequest`/`RunSummaryDto`, `CreditStatusDto`/`CreditLedgerEntryDto`,
    `ProvenanceCertDto`/`ProvenanceVerifyDto`. Add optional `code` to `ApiResponse`.
0.3 `IReleaseTrackGuard` (ownership) + `IReleaseEntitlementService` (plan resolve) + credit
    ledger service with the race-safe debit/refund SQL (§2.7).

**Phase 1 — FREE surface (Agent B)**
1.1 `IReleaseReadinessService` + readiness rules (§6) + `IAudioAnalysisService` (ffmpeg measure;
    Agent A provides the ffmpeg wrapper, see 2.1). 1.2 `TrackAiDisclosure` service + DDEX export (§8/§2.1).
1.3 `ReleaseReadyController` (FREE endpoints §3.1–3.2) + credit read endpoints §3.3.
1.4 Tests: readiness pass/warn/fail per rule; disclosure validation + `disclosureCompleted` logic.

**Phase 2 — audio/image engines (Agent A)**
2.1 `IAudioAnalysisService` (ffprobe/ebur128 wrapper) — shared by readiness + mastering.
2.2 `IAudioMasteringService` (two-pass loudnorm, §7) → new asset + metrics.
2.3 `ICoverArtService` (ImageSharp validate/generate, §8) → new asset.
2.4 Tests: loudnorm targets within tolerance on a fixture; cover validate/generate dimension+colorspace.

**Phase 3 — paid runs + worker (Agent A)**
3.1 `IReleaseReadyRunService` (charge → create run → enqueue; idempotent; §3.4/§5).
3.2 `ReleaseReadyWorker` (`BackgroundService`, claim/execute/status/refund/crash-recovery, §4).
3.3 Run + result download endpoints (§3.4–3.6).
3.4 Tests: charge-once, insufficient-credits 403, idempotent replay, refund-on-failure, step status
    transitions (use a fake ffmpeg/image service so tests don't need the binary).

**Phase 4 — provenance certificate (Agent B)**
4.1 Refactor shared PDF chrome → `CertificatePdfTheme`; regression-test `LicensePdfGenerator`.
4.2 `IProvenanceCertificateService` + repository + `ProvenancePdfGenerator`; issue as run step.
4.3 Certificate retrieval/PDF/verify endpoints (§3.7) + public v1 verify (§3.8) + OpenAPI/manifest +
    `validate-contracts.cjs`.
4.4 Tests: cert requires completed disclosure (409), attestation captured, verify returns non-PII,
    PDF renders, public verify works anonymously.

**Phase 5 — Stripe credit top-up (Agent B, needs approval for webhook)**
5.1 `POST /release-ready/credits/checkout` (Checkout Session w/ metadata).
5.2 Webhook fulfillment branch (PROTECTED) — atomic ledger credit + `StripeEventId` dedupe.
5.3 Tests: webhook grants credits once (idempotent on replay), balance reflects grant, ledger audit.

**Phase 6 — infra + cross-cutting**
6.1 Dockerfile ffmpeg; 6.2 DI wiring + hosted service; 6.3 docs (`docs/` API tutorial entry);
6.4 ensure new internal routes don't break `validate-contracts.cjs`/contract drift tests.

Every bug fix during implementation must ship with a failing-then-passing test (repo regression
policy). Touching payments/entitlements/auth/migrations requires tests (CLAUDE.md agent rules).

---

## 12. Open questions & assumptions

**Assumptions (proceed unless told otherwise):**
1. Mastering targets **−14 LUFS / −1 dBTP / LRA 11**; readiness loudness bands per §6. *Tunable via config.*
2. Mastering output = **MP3 320 kbps**; produces a **new** asset; never overwrites; adoption is opt-in.
3. Cover compliance = **3000×3000, square, sRGB JPEG**; `generate` pads (not crops) non-square.
4. Pro = `CreatorTier.Pro || Tier=="pro"` (matches `CapabilityResolver`); no separate active-sub check.
5. Credit = **1 per run** (any number of paid steps); refunded on total failure.
6. Provenance PDFs are **stored** in object storage for reproducible verification.
7. In-process `BackgroundService` worker is acceptable for current single-instance Render topology.
8. Duplicate detection v1 = title+duration+object-hash heuristic (no acoustic fingerprint).

**Open questions (need a decision before/within implementation):**
- **Q1 (DDEX):** exact DDEX field/version for AI disclosure (ERN 4.x AI flags / contributor roles).
  §2.1/§8 define a stored shape + `DdexPayload` export; the precise DDEX tag mapping must be confirmed
  against the DDEX AI-disclosure spec the distributor expects. *Owner: product/distribution.*
- **Q2 (Render worker):** is in-process background processing OK on the current instance size, or do we
  provision a separate Render worker service (cost) for ffmpeg load and to avoid request-thread CPU
  contention? Affects §4/§10. *Owner: infra.*
- **Q3 (Duration source):** `Track.Duration` is a free-text string and may be absent/unreliable;
  readiness will prefer ffprobe. Confirm we can afford the read on every scan or cache it on the track.
- **Q4 (ImageSharp license):** confirm the ImageSharp version/license is acceptable for commercial use
  (Apache-2.0 vs Six Labors Split License). Alternative: SkiaSharp (native deps) or Magick.NET.
- **Q5 (Credit packs & prices):** pack sizes (`single`/`pack5`/`pack20`) and **cent prices** + Stripe
  Product/Price ids. Needed for §3.3/§10.4. *Owner: product.*
- **Q6 (Internal contract coverage):** must `/release-ready/*` internal endpoints be added to
  `contracts/openapi.v1.json` (and pass `validate-contracts.cjs`), or is that file only for the public
  `/api/v1` surface? Determines Phase 4.3/6.4 scope.
- **Q7 (`adoptMasterAsCatalogAudio`):** is overwriting the catalog's playable audio with the master a
  v1 feature, or defer? It mutates a live catalog field (with original retained) — confirm UX.
- **Q8 (Frontend design system):** confirm the component library/design tokens in the separate Next.js
  repo so UI tickets match house style (not visible from this backend repo).
- **Q9 (Cert revocation):** who can revoke a provenance certificate and on what grounds
  (false attestation)? `RevokedAt` exists; the policy/endpoint is unspecified for v1.

---

## 13. Completeness self-check

- **Stack confirmed:** .NET 8 / ASP.NET Core; EF Core 8 + Npgsql/Postgres; Identity + JWT; Stripe +
  Pro; `IObjectStorage`→Supabase S3; QuestPDF; in-memory cache (no Redis); **no existing background
  worker (designed here)**; **no existing credit ledger (designed here)**; license-cert system mapped
  and reused; Next.js/React frontend (separate repo). ✔
- **API contract complete:** every endpoint has method, path, auth, entitlement gate, request body,
  response body, and error cases — FREE (readiness ×2, disclosure ×2), credits (read, ledger,
  checkout), paid run (create, get, list), result downloads (master, cover), provenance (get, pdf,
  verify, public v1 verify); error-code catalog in §3.9. ✔

**This document is the shared source of truth.** Implementers: if reality diverges from any
assumption here, update this file in the same PR rather than diverging silently.
