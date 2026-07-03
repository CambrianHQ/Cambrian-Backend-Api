# Release Ready v1 — FROZEN API CONTRACT (shared build artifact)

**This file is the single source of truth for the parallel build. Do not deviate.**
Frozen 2026-06-06. Backend, frontend, and tests are built concurrently against it.

## Already built (DO NOT recreate — depend on these)

Backend (`Cambrian-Backend-Api`):
- Entity `Cambrian.Domain.Entities.MasteringJob` (status machine; `ChargedAt` = credit ledger), Track DDEX cols `AiGenerated` / `AiDisclosureDdex`. EF config + migration applied.
- `IMasteringEngine` (+ `FfmpegEngine` default, `TonnEngine` RoEx, config-switched via `Mastering:Engine`). DTOs `MasteringEngineRequest/Result`.
- `IReleaseValidationService` (+ `ReleaseValidationService`): `ValidateMetadata(Stream,fileName)`, `ValidateArtwork(Stream?,fileName)`. DTOs `ValidationReport` / `MetadataValidationResult` / `ArtworkValidationResult`.
- **Frozen interfaces/DTOs (implement against, do not edit):** `IReleaseCreditService`, `IMasteringJobRepository`, `IReleaseReadyService`, `Exceptions/InsufficientCreditsException`, `DTOs/ReleaseReady/ApiModels.cs` (`CreditStatusDto`, `ValidateResponse`, `JobDto`, `JobSummaryDto`, `ReleaseReadyUploadInput`, `MasteringDownload`).
- `TierManifest.ReleaseReadyCreditsPerMonth` (Free 0, Creator 3, Pro 10 - credit allowance source).
- `IObjectStorage` (storage): `UploadAsync(stream,key,contentType)`, `GenerateDownloadUrl(key,filename)`, `OpenReadAsync(key)`. Reuse the existing Supabase S3 config.
- Sentry is wired (`SentrySdk.CaptureException`).

## Flow
Upload → validation report → **submit** (ffmpeg: charges + masters in one shot) **or** preview+**approve** (Tonn: charges on approve) → poll status → download. Credit balance visible throughout.

## HTTP endpoints — controller `ReleaseReadyController : BaseController`, `[Route("release-ready")]`, all `[Authorize]`
Envelope: success `{ success:true, data }`; Release Ready validation/credit failures return `{ success:false, error:{ code, message, field?, details? }, errors?: [{ code, message, field?, details? }], validation? }`. User id via `GetRequiredUserId()`. Ownership: a job not owned by the caller -> 404 (`not_found`).

| # | Method + path | Body | 200 data | Errors (code) |
|---|---|---|---|---|
| 1 | `GET /release-ready/credits` | — | `CreditStatusDto` | 401 unauthenticated |
| 2 | `POST /release-ready/validate` | multipart: `audio` (file, req), `artwork` (file, opt), `trackId` (guid, opt), `aiGenerated` (bool, opt), `aiDisclosure` (string, opt), `targetLufs` (double, opt) | `ValidateResponse` | 400 validation_failed/audio_too_short/audio_too_long/invalid_audio/missing_cover_art/missing_metadata; 401 |
| 3 | `POST /release-ready/jobs/{id}/submit` | — | `JobDto` | 404 not_found; 409 invalid_state; 403 insufficient_credits; 401 |
| 4 | `POST /release-ready/jobs/{id}/approve` | — | `JobDto` | 404; 409 invalid_state; 403 insufficient_credits; 401 |
| 5 | `GET /release-ready/jobs/{id}` | — | `JobDto` | 404; 401 |
| 6 | `GET /release-ready/jobs?take=20` | — | `JobSummaryDto[]` | 401 |
| 7 | `GET /release-ready/jobs/{id}/download?format=wav\|mp3` | — | 302 → signed URL **or** streamed file (match `DownloadController`) | 404; 401 |

**Release Ready error code catalog**: `validation_failed`, `audio_too_short`, `audio_too_long`, `invalid_audio`, `missing_cover_art`, `missing_metadata`, `insufficient_credits`, `duplicate_submission`, `email_not_verified`, `storage_error`, plus legacy/shared API codes `unauthenticated`, `not_found`, and `invalid_state`.

**openapi.v1.json** — add these EXACT path keys (validate-contracts is fail-build): `/release-ready/credits` (get), `/release-ready/validate` (post), `/release-ready/jobs/{id}/submit` (post), `/release-ready/jobs/{id}/approve` (post), `/release-ready/jobs/{id}` (get), `/release-ready/jobs` (get), `/release-ready/jobs/{id}/download` (get). Mirror the simple style of existing entries (tags `["ReleaseReady"]`, path params, `responses: {200:{description:OK}}`).

## Credit rules (exact)
- Allowance = `TierManifest.For(user.CreatorTier).ReleaseReadyCreditsPerMonth`.
- Used = count of this creator's jobs with `ChargedAt` in the **current calendar month UTC** AND `Status != 'failed'` (failed jobs release the credit; audit row stays).
- Remaining = max(0, Allowance − Used).
- **Charge timing:** ffmpeg → on **submit**; Tonn → on **approve**.
- **Atomic:** `TryChargeAsync` runs count-and-set-`ChargedAt` in ONE serializable transaction (or conditional update) so two concurrent submits cannot both pass with one credit left. Idempotent per job (already-charged job → true, no double charge). Remaining 0 → returns false → caller throws `InsufficientCreditsException` → 403.

## ffmpeg verification
- Command: `dotnet test Cambrian.sln --configuration Release --no-build --filter "FullyQualifiedName~FfmpegEngineMetadataTests"`.
- Requires `ffmpeg` on `PATH`. If missing, the test prints `ffmpeg verification skipped` with this command and returns without running the binary metadata assertions.
- The test verifies MP3 title/artist/album metadata, metadata argument injection safety, embedded cover art, 320 kbps MP3 output, and 44.1 kHz/16-bit WAV output.

## Storage keys
- source: `release-ready/source/{jobId}{ext}`; master WAV: `release-ready/master/{jobId}/master.wav`; MP3: `release-ready/master/{jobId}/master.mp3`. Never modify the source key.

## Worker (`MasteringWorker : BackgroundService`, register `AddHostedService`)
Poll ~3s; claim queued race-safely (`ClaimNextQueuedAsync`); new DI scope per job; run `IMasteringEngine.MasterAsync`. ffmpeg → upload wav+mp3, set keys + measured LUFS/TP, status `done`. Tonn → store preview, status `awaiting_approval` (final retrieved in `ApproveAsync` via `FinalizeAsync`). **One retry**: first failure → requeue (`RetryCount=1`); second → status `failed`, `SentrySdk.CaptureException`. Bound ffmpeg concurrency with a `SemaphoreSlim(1)`.

## Frontend HTTP shapes (camelCase JSON; unwrap `{success,data}`)
- `creditStatus`: `{ allowance, used, remaining, plan }`
- `validateResponse`: `{ jobId, engine, requiresApproval, validation: { metadata:{ passed, title, artist, album, issues[], stripped[] }, artwork:{ passed, provided, width, height, format, issues[] } } }`
- `job`: `{ id, trackId, engine, status, requiresApproval, validation, inputLufs, outputLufs, outputTruePeakDbtp, previewUrl, wavReady, mp3Ready, error, createdAt, completedAt }`
- `jobSummary`: `{ id, status, engine, createdAt, completedAt }`

## File ownership (NO two agents touch the same file)
- **backend-impl**: `src/**` new services/repo/worker/controller + Program.cs DI + `contracts/openapi.v1.json` + `Dockerfile`. (backend repo)
- **backend-tests**: `tests/**` only. (backend repo)
- **frontend-feature**: `app/api/releaseReady.ts`, `app/api/index.ts` (register), `app/release-ready/page.tsx`, `views/ReleaseReadyPage.tsx` (+ any new components under `components/release-ready/`), `app/hooks` only if adding a new file. (frontend repo)
- **copy-pr4**: `views/LandingPage.tsx`, `views/AboutPage.tsx` ONLY. (frontend repo)
