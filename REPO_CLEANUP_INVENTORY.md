# REPO_CLEANUP_INVENTORY.md

> Pre-cleanup inventory for the production-readiness cleanup pass.
> Branch: `repo/production-cleanup` (created from `feat/residue-be`).
> Generated: 2026-06-18. **Read-only discovery — no source changes made yet.**
> Legend for the **Action** column:
> `KEEP` = leave as-is · `SAFE` = proven safe to remove/edit · `GATED` = removable but needs explicit sign-off (touches protected/payment paths) · `DO-NOT-TOUCH` = out of scope for a cleanup pass.

---

## 0. Scope corrections (the brief vs. this repo)

Two assumptions in the cleanup brief do **not** hold for this repository. They reshape the whole job:

1. **There is no frontend in this repo.** It is a backend-only .NET 8 solution. The only `package.json` files are dev tooling (`/package.json`, `scripts/email-pro-upgrade`, `tools/audio-rehydrate`). There are no React components, hooks, pages, Next build, or `tsc` project. **The entire "Frontend cleanup" section is N/A here** — it belongs to a separate frontend repo.

2. **"Marketplace / licensing / exclusive / buyout" is NOT obsolete dead code here.** It is live, load-bearing pricing/upload/persistence code — 486 references across 87 source files. CLAUDE.md explicitly protects exclusive/copyright-buyout as **active atomic money-movement**. A scrub is *already in progress* on `feat/residue-be` (the public catalog `TrackResponse` was already reduced to NonExclusive-only). Removing the rest is a **product migration**, not a cleanup, and is **GATED** (see §10). Only a tiny, genuinely-dead subset is `SAFE`.

---

## 1. Top-level folders & files

| Path | What it is | Action |
| --- | --- | --- |
| `src/` | 5 backend projects (Domain/Application/Persistence/Infrastructure/Api) | KEEP |
| `tests/` | `Cambrian.Api.Tests` (single xUnit project, ~115 test files) | KEEP |
| `contracts/` | Canonical OpenAPI + manifests + policy (protected) | DO-NOT-TOUCH |
| `governance/`, `policy/`, `manifests/`, `architecture/` | Governance docs & compliance rules | KEEP (one stale doc, §13) |
| `scripts/` | Build/contract/test/ops scripts (mixed live + one-off) | mixed, §5 |
| `qa/`, `loadtests/`, `postman/`, `seed/`, `observability/`, `grafana/` | QA collections, k6, seeds, monitoring | KEEP |
| `tools/` | `audio-rehydrate` (active), `export-postman`, `FixMigrations` | KEEP |
| `config/` | `secrets.enc.env` (SOPS-encrypted), `provenance-signing.key.enc`, `resend-mcp.example.json` | DO-NOT-TOUCH |
| `docs/` | Developer docs + tutorials (several stale, §13) | mixed, §13 |
| `reports/` | `audio/` incident + rehydration reports (untracked, local) | KEEP |
| `.github/`, `.githooks/` | CI workflows (ci, deploy-staging/production, export-postman, resilience) + pre-commit/pre-push hooks | KEEP |
| `.claude/worktrees/billing-v2/` | **A live git worktree** (branch `feat/billing-v2`), gitignored | DO-NOT-TOUCH (deleting corrupts git worktree state) |
| **Root scratch docs:** `AUDIT_REPORT.md`, `BACKEND_BRIEF.md`, `CREATOR_PROFILE_AUDIT.md`, `LOCAL_PAID_SETUP.md` | One-off audits/briefs (tracked) | SAFE to relocate/archive, §7 |
| **Root data/fixtures:** `prod-tracks.txt`, `test-audio.wav`, `test-beat.mp3`, `test-golden.mp3`, `migrate-storage.mjs` | Orphan prod-data dump + unused audio fixtures + superseded one-off script | SAFE to remove, §7 |
| **Root logs:** `api-local.log` (2.2 MB), `verify-backend.log`, `stripe-listen*.log`, `api-local.err.log` | Local run logs — **already gitignored, NOT tracked** | KEEP (nothing to do; local-only) |
| `API.md`, `README.md`, `SECRETS.md`, `CLAUDE.md` | Maintained docs | KEEP (README API-URL fix, §13) |
| `Cambrian.sln`, `Directory.Build.props`, `global.json`, `NuGet.Config`, `Dockerfile`, `.dockerignore`, `render.yaml`, `docker-compose*.yml` | Build/deploy (protected) | DO-NOT-TOUCH |
| `verify.ps1`, `run-local.sh` | Local helper scripts | KEEP (verify references, §5) |
| `.tmp/`, `.dotnet-home/`, `.playwright-mcp/` | Local scratch dirs — not tracked | KEEP |

---

## 2. Backend (`src/`) layout

Standard clean DDD layering — no structural reorg needed:

- `Cambrian.Domain/` — entities, enums, `Auth/Capabilities.cs`, domain constants.
- `Cambrian.Application/` — DTOs, service/repo interfaces, services, `AI/Discovery/*`.
- `Cambrian.Persistence/` — `CambrianDbContext`, configurations, repositories, **38 migrations (immutable)**.
- `Cambrian.Infrastructure/` — Stripe, storage (R2/S3/local), email, SMS, mastering.
- `Cambrian.Api/` — 38 controllers, middleware, `StartupExtensions.cs`, `E2e/` (isolated), `Mcp/`, `BackgroundServices/`.

**Folder organization is already sound.** No backend file-moves recommended.

---

## 3. Test folders

The test suite is **already well-organized by concern + layer** and needs no reorganization:

```
tests/Cambrian.Api.Tests/
  AI/ Contract/ Fixtures/ Integration/Api/ Invariants/ Observability/
  Payments/ PerformanceHooks/ Regression/ Security/ Unit/Application/ Webhooks/
  (+ ~84 root-level test files grouped by feature name)
```

- **Live tests:** exactly one, already isolated outside the test assembly — `tools/audio-rehydrate/browser/audio-playback.spec.ts` (Playwright).
- **E2E support tests:** `Integration/Api/E2eSupportEndpointsTests.cs`, `Unit/Application/E2eSupportTests.cs` (exercise the isolated `src/Cambrian.Api/E2e/` surface).
- **Security tests:** already under `Security/` + `Regression/` (Authorization, RoleAccessMatrix, VerifiedEmailPolicy, PaymentRateLimiting, AiDiscoverySecurity, LicensingLeak).
- **Relational (Testcontainers/Postgres) tests** — 4 classes: `CheckoutAndBillingApiTests`, `AuthMigrationRegressionTests`, `ReleaseReadyCreditRegressionTests`, `ReleaseReadyCreditTests`. (These can crash `dotnet test` without Docker — exclude when Docker is down.)
- **No true duplicate tests found** — apparent overlaps (auth, webhooks, release-credit) are intentional layer coverage (unit ↔ HTTP ↔ relational).

**Recommendation:** Do **not** move tests into the brief's `tests/live`, `tests/e2e/local-release-gate`, `tests/e2e/security` folders. The current structure is cleaner, and re-pathing would churn `git blame`, namespaces, and fixtures for zero benefit. (Documented as a deliberate deviation from the brief.)

---

## 4. Scripts (`scripts/` + root)

| Script | Referenced by | Action |
| --- | --- | --- |
| `validate-contracts.cjs` | pre-commit hook, CI, package.json | KEEP (live) |
| `check-contract-drift.cjs` | package.json, CLAUDE.md | KEEP |
| `detect-breaking-changes.cjs` | CI | KEEP |
| `generate-endpoint-manifest.cjs` | — only `AUDIT_REPORT.md` | GATED (verify not run manually before removing) |
| `extend-openapi.cjs`, `patch-openapi.cjs`, `patch-openapi-avatar.cjs`, `patch-openapi-routes.cjs`, `patch-openapi-contract.cjs`, `fix-stale-openapi.cjs`, `reconcile-contracts.cjs` | only `AUDIT_REPORT.md` | GATED — one-off OpenAPI-repair tooling; likely dead, but confirm no manual runbook before deleting |
| `detect-email-leaks.cjs` | only `AUDIT_REPORT.md` | GATED |
| `start-backend.ps1`, `start-resend-mcp.ps1`, `print-resend-mcp-config.ps1` | package.json / docs | KEEP |
| `staging-smoke.ps1` | `qa/PRE_DEPLOY_CHECKLIST.md` | KEEP |
| `pre-deploy-tests.ps1` | README, POLICY.md | KEEP |
| `test-critical.ps1`, `test-full.ps1` | (hooks run `dotnet test --filter` directly) | GATED — possibly redundant wrappers; confirm before removing |
| `validate-openapi.ps1`, `dotnet-local.ps1`, `test-all-endpoints.ps1`, `seed-local.sql` | **no references anywhere** | GATED — likely dead, but PS/SQL helpers may be manual tools |
| `migrate-storage.mjs` (root) | only `AUDIT_REPORT.md` (which itself says "decide if it belongs") | SAFE to remove/archive — superseded by `tools/audio-rehydrate` |

> Note: "referenced only in AUDIT_REPORT.md" is weak evidence of death (a human may still run them). These are marked GATED rather than SAFE.

---

## 5. Config files

| File | Status |
| --- | --- |
| `.env` | **Not tracked** (gitignored). ✓ |
| `.env.example` | Tracked; values are literal `***REDACTED***` placeholders — **no real secrets**. ✓ |
| `config/secrets.enc.env` | Tracked but **SOPS/age-encrypted** (`ENC[AES256_GCM…]`). ✓ |
| `config/provenance-signing.key.enc` | Encrypted key. ✓ |
| `docs/env-contract.md` | Env-var reference doc — current. |

**Secrets posture is clean. No plaintext secrets committed.** E2E surface (`src/Cambrian.Api/E2e/`) — verify fail-closed in Production/Staging during execution (§14 follow-up).

---

## 6. Generated / build artifacts

- `bin/`, `obj/` — gitignored (not tracked). No action.
- `contracts/openapi.v1.json`, `endpoint-manifest.v1.json` — **committed generated contracts the build/tests depend on**. DO-NOT-TOUCH.
- `package-lock.json` — keep (tooling lockfile).
- `tools/audio-rehydrate/node_modules/` — should be gitignored; verify it isn't tracked (follow-up).

---

## 7. Duplicate / near-duplicate & orphan files (SAFE candidates)

| File | Why removable | Evidence |
| --- | --- | --- |
| `prod-tracks.txt` | Orphan production catalog dump (titles → R2 keys); **0 references** in code/scripts | grep clean |
| `test-audio.wav`, `test-golden.mp3` | Unused audio fixtures; **0 references** | grep clean |
| `test-beat.mp3` | Only appears as a *string* `AudioUrl = "tracks/test-beat.mp3"` in fixtures — **never opened from disk** (tests mock `OpenReadAsync`) | `CambrianApiFixture.cs:284,431`, `RelationalCambrianApiFixture.cs:268` |
| `migrate-storage.mjs` | Superseded one-off storage migration | §4 |
| Root one-off reports: `AUDIT_REPORT.md`, `CREATOR_PROFILE_AUDIT.md` | Point-in-time audits; belong under `reports/` not repo root | — |
| `.claude/worktrees/billing-v2/*` | Full duplicate of the repo — but it is a **live worktree**, not a stray copy | DO-NOT-TOUCH (see §1) |

---

## 8. Suspected dead source files/code

| Symbol / file | Verdict | Evidence |
| --- | --- | --- |
| `src/Cambrian.Domain/Entities/License.cs` | **DEAD — SAFE** | Orphan entity; no DbSet, no config, no caller. Only self-reference. |
| `src/Cambrian.Domain/Enums/LicenseType.cs` (enum `Standard/Extended/Exclusive/CopyrightBuyout`) | **DEAD — SAFE** | Referenced *only* by `License.cs`. (The pervasive `Track.LicenseType`/`Purchase.LicenseType` are a separate `string` property; `QuestPDF.Settings.License = LicenseType.Community` is QuestPDF's own enum.) No test depends on it. |
| `ITrackRepository.TryMarkExclusiveSoldAsync` + `TrackRepository.cs:602` impl | DEAD but **GATED** | No callers anywhere; no test references it. BUT it is the atomic exclusive-sale SQL update — CLAUDE.md protects this path. Remove only with explicit sign-off, ideally in the residue migration. |

---

## 9. Obsolete marketplace / licensing — ACTIVE vs DEAD (the centerpiece)

The brief lists many terms to "find and remove." Here is the truth in this repo:

**Genuinely DEAD (SAFE):** `License` entity, `LicenseType` enum (§8).

**ACTIVE / TRANSITIONAL — DO-NOT-TOUCH in a cleanup pass (requires coordinated migration + tests):**
- `Track.ExclusivePriceCents`, `Track.CopyrightBuyoutPriceCents`, `Track.ExclusiveSold`, `Track.CopyrightOwnerId/TransferredAt/OriginalCreatorId` — **written** by `UploadService` & `CreatorController` edit, **persisted** via raw-SQL `Insert/UpdateLegacyCompatibleTrackAsync`. `!ExclusiveSold` is a filter in **every public catalog query** (`TrackRepository`, `CreatorIdentityRepository`).
- `src/Cambrian.Api/Common/TrackPricingSnapshot.cs` — **still serializes `exclusivePrice`/`copyrightBuyoutPrice` to authenticated `/creator` & `/users` endpoints** (the most live "residue leak"). Public `TrackResponse` was already scrubbed; these two anonymous-object endpoints were not.
- `UploadTrackRequest` / `EditTrackRequest` — **still accept** `ExclusivePrice(Cents)` / `CopyrightBuyoutPrice(Cents)` and persist them.
- `AiLicenseOptionDto` + `TrackAiResponseBuilder` — **served on the public AI-discovery & MCP surface** (emits a single "Standard Usage" option; framing is residue but it's live).
- `MarketplaceIntegrityService` — registered (`Program.cs:403`), served at `GET /admin/integrity`.
- `Capabilities.TrackLicenseExclusive`/`TrackLicenseBuyout` — intentional **retired tombstones** (tests assert they never appear). `LicensePurchase` capability is still granted.

**Confirmed non-existent (0 hits):** `IsCopyrightTransferred`, `ExclusivePlatformFee`, `ExclusiveCreatorEarnings`, `CopyrightBuyoutPlatformFee`, `CopyrightBuyoutCreatorEarnings`. Only `NonExclusive*` variants exist.

**The Stripe webhook fulfillment path contains no exclusive/buyout/license logic** — money movement is already fully non-exclusive/subscription.

➡️ **Recommendation:** finishing the residue removal (DTO fields, `TrackPricingSnapshot`, dead schema columns) should be a **dedicated, test-backed migration PR on the `feat/residue-be` line**, not this cleanup pass — it changes API responses and DB schema and touches protected payment code.

---

## 10. Suspected unused backend services/DTOs/endpoints

Beyond §8/§9, no additional clearly-dead services/DTOs were proven in this pass. A full unused-symbol sweep across all DTOs/services is a larger task (flagged as a follow-up, §14) and was not rushed here to avoid false positives.

---

## 11. Stale tests

- **No obsolete-product tests to delete.** Tests that mention `"exclusive"`/`LicenseType` (e.g. `LegacyTrackWriteCompatibilityTests`, `DatabaseConsistencyTests`, `LicensingLeakRegressionTests`) are **legacy-compatibility / leak-regression guards** — exactly what the brief says to KEEP.
- Known failing tests per CLAUDE.md (AI schema contract, `/sse` OpenAPI paths) are **contract drift**, not obsolete expectations — classify as real-bug/config, do not delete (§14).

---

## 12. Stale docs (SAFE edits)

| Doc | Problem | Fix |
| --- | --- | --- |
| `docs/README.md`, `docs/quickstart.md`, `docs/tutorials/*` (×3), `README.md` | Use `api.cambrianmusic.com` as the **live base URL** | Replace with `https://cambrian-backend-api.onrender.com` |
| `docs/DEPLOYMENT.md` | Describes **Railway** ("replaces render.yaml") + old API URL; project actually deploys on **Render** | Rewrite to Render + correct URL |
| `architecture/ARCHITECTURE.md` (line 10) | "Cambrian is a **music licensing marketplace**" — pre-pivot product model | Update overview to current product (creator legitimacy/compliance + subscriptions + Release-Ready + provenance) |
| `BACKEND_BRIEF.md` §8 | Documents legacy marketplace — but **explicitly marked deprecated** | KEEP (clearly labeled historical) |

**Do NOT change** `api.cambrianmusic.com` in: `tools/audio-rehydrate/**` (intentional **dead-origin** regression guards), nor `postman/`, `qa/bruno/`, `loadtests/` env files without separate confirmation (those are `staging-api.cambrianmusic.com` test envs — different question; §14).

---

## 13. Risky files — DO NOT TOUCH without explicit confirmation

- **Payments/money:** `StripeWebhookService.cs`, `PayoutService.cs`, wallet/earnings logic, `TrackPricingSnapshot`, all exclusive/buyout active code (§9).
- **Migrations:** every file under `src/Cambrian.Persistence/Migrations/` + `CambrianDbContextModelSnapshot.cs` (immutable schema history).
- **Contracts:** `contracts/openapi.v1.json`, `endpoint-manifest.v1.json`.
- **Git worktrees:** the 4 active worktrees incl. `.claude/worktrees/billing-v2/`.
- **Deploy:** `render.yaml`, `Dockerfile`, `docker-compose*.yml`, `.github/workflows/deploy-*`.
- **Secrets:** `config/secrets.enc.env`, `config/provenance-signing.key.enc`, `.env`.
- **In-flight WIP:** the 9 uncommitted files on `feat/residue-be` (subscription idempotency + residue scrub) — do not stomp.

---

## 14. Follow-ups (out of scope for the safe pass)

1. Residue migration PR: remove exclusive/buyout DTO fields, `TrackPricingSnapshot` residue, dead schema columns — with a new EF migration + tests (the regression policy requires tests).
2. Full unused-DTO/service symbol sweep (Roslyn/`dotnet` analyzer) — too broad to rush safely.
3. Fix the known failing contract tests (AI schemas, `/sse` paths) — real drift, needs a contract sync.
4. Confirm E2E fail-closed in Production/Staging; confirm `tools/audio-rehydrate/node_modules` is gitignored.
5. Decide fate of GATED one-off OpenAPI/email scripts (§4) after confirming no manual runbook depends on them.
