# CLAUDE.md - Cambrian Backend API

Last verified from this checkout: 2026-07-06.

This file is an operating brief for AI/code agents. Treat it as a snapshot of the current codebase, not a product promise. If a statement here matters for a change, verify it against source before editing.

## Current Git State

- Local repo: `C:\Users\logan\Cambrian-Backend-Api`
- Current local branch when this file was updated: `feat/entitlements`
- Current `HEAD`: `49fc5cf`
- `origin/staging` also points at `49fc5cf`
- `origin/feat/entitlements` points at `7ebdad3`; local `feat/entitlements` is ahead because the staging publish merged `origin/staging` and pushed `HEAD` directly to `staging`.
- Remote: `git@github.com:CambrianHQ/Cambrian-Backend-Api.git`

## Truthful Validation State

Verified during the test-debt cleanup pass (2026-07-06):

- `dotnet build Cambrian.sln --configuration Release` passes clean: `0 Warning(s)`, `0 Error(s)`.
- `dotnet test Cambrian.sln --configuration Release` is green: `1098 passed`, `4 skipped`, `0 failed`, total `1102`.
  - The 4 skips are `Cambrian.Api.Tests.StabilityTests` upload-validation tests, each with an explicit tracking reason: "Phase A validation reverted in c7e31bf — restore UploadService title/price guard to re-enable."
- `node scripts/validate-contracts.cjs` exits successfully (non-blocking), reporting seven architecture-compliance warnings — see "Contracts And Governance" below for the current list.
- `node scripts/check-contract-drift.cjs` exits `0` with "Contract drift checks passed." — no drift, no false positives.
  - **Stale as of the charts-and-rankings audit (2026-07-13):** this script now exits `1` with 9 pre-existing failures unrelated to charts (Albums/BehindTheTrack/proof-videos/lyrics status-code and route-parity drift that landed in commits after 2026-07-06). Verified this is baseline drift, not something the charts audit introduced, by running the script against `HEAD` with and without the charts changes stashed — identical failure set both times. Not fixed as part of the charts audit (out of scope); needs its own pass.

Previously known failures, now resolved:

- `Cambrian.Api.Tests.AI.AiDiscoveryContractTests` (30 tests): the AI Discovery controller returned anonymous/untyped `IActionResult` payloads, so Swashbuckle never emitted `Ai*` schemas into `contracts/openapi.v1.json`. Fixed by adding typed response DTOs (`AiTrackDetailsResponse`, `AiTrackPreviewResponse`, `AiCreatorProfileResponse`, `AiTrackLicenseOptionsResponse`) and `[ProducesResponseType]` attributes on `AiDiscoveryController`, renaming the AI Discovery DTOs to drop their `Dto` suffix (matching the schema names the tests expect and the no-suffix convention already used elsewhere in the contract), and adding the previously-unwired `GET /ai-discovery/tracks/{trackId}/licenses` endpoint so `AiTrackLicenseOptionsResponse` has a real caller. The contract was then regenerated via the Swashbuckle CLI (`dotnet swagger tofile`) and copied in as raw text (never round-tripped through `JSON.parse`/`JSON.stringify`, which would corrupt the `decimal.MaxValue` literals in the money schemas).
- The `/sse` `OpenApi_Paths_Have_Matching_Controller_Actions` / `GET /sse` `307` failures mentioned in an earlier revision of this file are no longer reproducible on this checkout.

Known validation/tooling caveats on this machine:

- Sandbox runs may fail with `.git/index.lock`, `.git/FETCH_HEAD`, or `.git/ORIG_HEAD.lock` permission errors. Git writes often need elevated execution.
- Sandbox `.NET` runs may fail on first-run sentinel writes under `C:\Users\CodexSandboxOffline`. If this happens, separate that machine failure from real compiler/test failures.
- `gitleaks` is not installed, so the pre-push hook currently warns and skips the secret scan.

## Stack

| Area | Current source of truth |
| --- | --- |
| Runtime | .NET 8 / ASP.NET Core |
| SDK pin | `global.json`: `8.0.100`, `rollForward: latestMajor`, `allowPrerelease: false` |
| Main project | `src/Cambrian.Api/Cambrian.Api.csproj` |
| Solution | `Cambrian.sln` |
| Database | PostgreSQL via EF Core/Npgsql |
| Identity | ASP.NET Core Identity plus JWT bearer |
| OAuth | Google ID token validation |
| Payments | Stripe / Stripe Connect via `Stripe.net` |
| Storage | Local dev storage plus S3-compatible storage through `AWSSDK.S3` |
| Email | Console, SMTP, and Resend implementations |
| OpenAPI | `contracts/openapi.v1.json` plus Swashbuckle |
| Tests | xUnit, FluentAssertions, NSubstitute, WebApplicationFactory, SQLite, Testcontainers PostgreSQL |

Important package versions currently referenced:

- `Microsoft.AspNetCore.Authentication.JwtBearer` `8.0.12`
- `Microsoft.EntityFrameworkCore.*` `8.0.12`
- `Npgsql.EntityFrameworkCore.PostgreSQL` `8.0.11`
- `Stripe.net` `46.2.0`
- `MailKit` `4.16.0`
- `QuestPDF` `2026.2.3`
- `Swashbuckle.AspNetCore` `6.6.2`
- `xunit` `2.9.3`
- `Microsoft.NET.Test.Sdk` `17.14.1`
- `Testcontainers.PostgreSql` `3.10.0`
- `FluentAssertions` `8.3.0`

There is no Redis dependency. Caching is in-process via `Microsoft.Extensions.Caching.Memory`.

## Repo Layout

```text
src/
  Cambrian.Domain/          Entities, enums, domain constants.
  Cambrian.Application/     DTOs, service/repository interfaces, business services.
  Cambrian.Persistence/     EF Core DbContext, configurations, repositories, migrations.
  Cambrian.Infrastructure/  Stripe, storage, email, SMS, external integrations.
  Cambrian.Api/             Controllers, middleware, startup, appsettings.

tests/
  Cambrian.Api.Tests/       Main test project. Current source files excluding bin/obj: 94.

contracts/
  openapi.v1.json           Canonical checked-in OpenAPI contract.
  endpoint-manifest.v1.json Endpoint manifest.
  policy.v1.json            Contract policy.

governance/
  backend-policy.v1.json    Architecture compliance rules used by scripts/validate-contracts.cjs.

manifests/
  BACKEND_MANIFEST.json
  FRONTEND_MANIFEST.json
```

Current counts from source:

- Controllers: 46 `.cs` files under `src/Cambrian.Api/Controllers`.
- EF migrations: 55 non-designer migration/model snapshot files under `src/Cambrian.Persistence/Migrations`.
- DbSets in `CambrianDbContext`: 36.
- OpenAPI paths: 243.
- OpenAPI operations: 268.
- OpenAPI schemas: 104.

## Current Feature Surface

The backend includes these major areas:

- Auth, registration, Google OAuth, password reset, admin seeding.
- Capability constants in `src/Cambrian.Domain/Auth/Capabilities.cs`.
- API key authentication and key management under `/api/v1/keys`.
- Creator identity/profile/storefront support.
- Catalog discovery with public track lookup by UUID and `CAMB-TRK-*` IDs through `/tracks/{trackId}`, `/track/{trackId}`, and `/catalog/{trackId}`.
- Track upload, image proxying, streaming, downloads, library, invoices, subscriptions, wallet, payouts.
- Stripe checkout/webhook purchase fulfillment, license certificate issuance, creator wallet crediting, and payout request flow.
- Public v1 API controllers under `src/Cambrian.Api/Controllers/v1`.
- Unified entitlements table and service layer:
  - `src/Cambrian.Domain/Entities/Entitlement.cs`
  - `src/Cambrian.Domain/Enums/EntitlementAccessLevel.cs`
  - `src/Cambrian.Domain/Enums/EntitlementResourceType.cs`
  - `src/Cambrian.Domain/Enums/EntitlementSourceType.cs`
  - `src/Cambrian.Application/Services/EntitlementService.cs`
  - `src/Cambrian.Persistence/Configurations/EntitlementConfiguration.cs`
  - `src/Cambrian.Persistence/Migrations/20260424004954_AddEntitlementsTable.cs`
- MCP/AI discovery code exists under `src/Cambrian.Application/AI/Discovery` and `src/Cambrian.Api/Controllers/AiDiscoveryController.cs`, including a `GET /ai-discovery/tracks/{trackId}/licenses` endpoint. The AI schema contract (`Cambrian.Api.Tests.AI.AiDiscoveryContractTests`) is green — the `Ai*` DTOs (no `Dto` suffix) and their `Ai*Response` wrappers are documented in `contracts/openapi.v1.json`.
- **Charts — "The Scene" weekly rankings** (hardened in the charts-and-rankings audit, 2026-07-13). Un-versioned routes, all in `src/Cambrian.Api/Controllers/ChartsController.cs`:
  - `GET /api/charts/weekly` — the running week (public).
  - `GET /api/charts/weekly/archive` / `GET /api/charts/weekly/archive/{isoWeek}` — permanent record of completed weeks (public).
  - `POST /admin/charts/aggregate` — recompute now (Admin).
  - `/charts/weekly` (no `/api` prefix, `Program.cs`) is a deliberately un-documented alias, excluded from OpenAPI.
  - One UTC window (`WeeklyChartService.StartOfIsoWeekUtc` — Monday 00:00 UTC, ISO week), one eligibility predicate (`WeeklyChartRepository.EligibleTracks` — public, `Status == "available"`, not `ExclusiveSold`, creator not suspended), one deterministic order (score desc → qualified plays desc → `Track.CreatedAt` desc → track id asc).
  - `Track.TrendingScore` is a dead column (default `0m`, never written by any production process — see `ICatalogService.cs`'s own doc comment) and the chart never reads it. Ranking candidates come directly from `StreamSessions` in the chart window on eligible tracks (`WeeklyChartRepository.GetQualifiedPlayCountsInWindowAsync`), not from a catalog "popular"/"trending" listing — a pre-audit version capped the candidate pool at a 200-track "popular" slice ordered by that same dead column, which could silently exclude a genuinely high-play track from the chart.
  - Recompute is scheduled (`WeeklyChartWorker`, `WeeklyChartService.RecomputeInterval` — currently 60s) and admin-triggerable; both call the same idempotent `AggregateAsync`. Reads carry `generatedAt`/`dataThrough`/`stale` so the frontend can show honest freshness rather than silently presenting old data as current.
  - Tests: `tests/Cambrian.Api.Tests/WeeklyChartRankingTests.cs` (deterministic ranking, tie-breaks, UTC boundary, archive pagination stability, eligibility exclusions, freshness), plus the pre-existing `WeeklyChartSnapshotTests.cs`, `WeeklyChartArchiveTests.cs`, `ChartsControllerTests.cs`.
  - `TrackStat`/`CreatorStat` (including `CreatorStat.TrendingScore`, a separate column from `Track.TrendingScore`) are declared entities with doc comments describing a "stats recompute job" that does not exist in this codebase — also dead, but out of scope for the charts audit since nothing chart-related reads them.

## Database And Migrations

`CambrianDbContext` inherits from `IdentityDbContext<ApplicationUser>`.

Current DbSets:

- `Tracks`
- `Creators`
- `Purchases`
- `Library`
- `Payouts`
- `Invoices`
- `AbuseReports`
- `AuditLogs`
- `Subscriptions`
- `StripeWebhookEvents`
- `StreamSessions`
- `WalletTransactions`
- `AnalyticsEvents`
- `FeatureFlags`
- `ActivityItems`
- `CreatorProfiles`
- `TrackCollections`
- `AlbumTracks`
- `TrackLyrics`
- `TrackCreationProcesses`
- `CreatorFollows`
- `ApiKeys`
- `Entitlements`
- `ApiIdempotencyKeys`
- `TrackBoosts`
- `ProvenanceAnchors`
- `TrackAuthorships`
- `MasteringJobs`
- `ReleaseCreditPurchases`
- `AuthorshipRecords`
- `EarningsTransactions`
- `FanSubscriptions`
- `TrackStats`
- `CreatorStats`
- `NewsletterSubscribers`
- `WeeklyChartSnapshots`

Recent migration area:

- `20260702210842_AddWeeklyChartSnapshots`
- `20260702211808_AddWeeklyDigestFields`
- `20260705181345_AddStudioSetupAndJourneyToCreatorProfile`
- `20260705194847_AddAlbumsLyricsBehindTheTrack`
- `20260407203714_AddPasswordResetAttemptTracking` exists and was repaired in staging history.

Migration rules:

- Do not edit migrations that may have been applied to any shared environment unless explicitly instructed.
- Prefer a new migration for corrective schema changes.
- Automatic migrations run on startup through `app.RunMigrationsAsync()` and are skipped in `Testing`.

## Contracts And Governance

`contracts/openapi.v1.json` is a checked-in contract and is enforced by `scripts/validate-contracts.cjs`.

Current validator state (2026-07-06):

- Contract has `243` routes.
- Validator finds `46` controller files.
- Validator reports seven non-blocking violations:
  - `AdminController.cs` imports domain entities instead of DTOs.
  - `AdminController.cs` contains business logic.
  - `CatalogController.cs` contains business logic (`.Select(...)` DTO projection).
  - `CreatorController.cs` contains business logic.
  - `CreatorProfileController.cs` contains business logic (a `.Where(...)` visibility filter inline in the action).
  - `src/Cambrian.Api/Controllers/v1/TracksV1Controller.cs` contains business logic.
  - `src/Cambrian.Application/DTOs/Email/ResendWebhookEvent.cs` is detected by the Stripe idempotency rule.

Because these currently pass as warnings, do not claim architecture compliance is clean. Also do not broaden this list without rerunning the validator. A prior revision of this file also listed a `CreatorProfileController.cs` "references DbContext directly" violation — that was a validator false positive (the regex matched the word "DbContext" inside a code comment, not an actual reference) and has been fixed in `scripts/validate-contracts.cjs`.

`scripts/check-contract-drift.cjs` currently passes cleanly ("Contract drift checks passed.") — it is run by `scripts/pre-deploy-tests.ps1` as a deploy gate but is not wired into `.github/workflows/ci.yml`.

Useful commands:

```powershell
node scripts/validate-contracts.cjs
node scripts/check-contract-drift.cjs
```

## Testing

Main test project: `tests/Cambrian.Api.Tests/Cambrian.Api.Tests.csproj`.

The suite now covers more than a single catalog test file. Current areas include:

- Auth/admin seed/regression
- API contract and OpenAPI coverage
- AI/MCP discovery
- Catalog and catalog service
- Checkout, billing, payments, purchases, webhooks
- Creator identity/profile/storefront
- Entitlements and entitlement performance hooks
- Library, downloads, stream
- Payouts and Stripe Connect readiness gates
- Relational invariants and migration application
- Public v1 license controller behavior

Validation commands:

```powershell
dotnet build Cambrian.sln --configuration Release --no-restore
dotnet test Cambrian.sln --configuration Release
dotnet test tests\Cambrian.Api.Tests\Cambrian.Api.Tests.csproj --configuration Release --filter "FullyQualifiedName~<TestName>"
```

When Docker is unavailable, relational tests that depend on PostgreSQL/Testcontainers may skip or fall back depending on fixture behavior. `RelationalCambrianApiFixture` is the current relational fixture entry point.

## Protected Systems

Get explicit human approval before modifying these areas unless the user has already named them as the target:

- Stripe webhook processing and purchase fulfillment:
  - `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs`
- Payout money movement:
  - `src/Cambrian.Application/Services/PayoutService.cs`
  - wallet transaction logic
- Stripe Connect configuration and key validation:
  - `src/Cambrian.Api/StartupExtensions.cs`
  - creator connect services
- EF schema and migrations:
  - `src/Cambrian.Persistence/CambrianDbContext.cs`
  - `src/Cambrian.Persistence/Migrations/`
- Public API contracts:
  - `contracts/openapi.v1.json`
  - `contracts/endpoint-manifest.v1.json`
- Deployment:
  - `render.yaml`
  - `Dockerfile`
- Fee tiers:
  - `src/Cambrian.Application/Configuration/TierManifest.cs`
- API key storage/auth:
  - `ApiKeyRepository`
  - `ApiKeysController`
  - `ApiKeyMiddleware`

## Money And Access Invariants

- Money values should be stored and displayed as cents where the contract expects cents. Do not introduce rounding shortcuts in backend monetary calculations.
- Creator wallet credit is fee-adjusted and tied to tier configuration. Do not hardcode platform fee rates.
- Exclusive and copyright buyout purchase paths rely on atomic state transitions. Do not replace race-protected SQL updates with naive tracked entity updates unless you are intentionally redesigning the concurrency model.
- Stripe webhook signature verification is required for real webhook processing.
- Stripe webhook idempotency is backed by `StripeWebhookEvents.EventId` and unique constraints. Do not remove event deduplication.
- API keys store hashes only. Raw key material is returned once at creation and must not be persisted or exposed later.
- Frontend authorization should trust backend-provided roles/capabilities, not local inference.
- Entitlement access levels are ordered; do not renumber `EntitlementAccessLevel` values.

## Public API v1

The public v1 controllers live in `src/Cambrian.Api/Controllers/v1`.

Current checked-in OpenAPI includes:

- `TracksV1`
- `CreatorsV1`
- `LicensesV1`
- API key routes under `/api/v1/keys`

For license work, start with:

- `src/Cambrian.Api/Controllers/v1/LicensesV1Controller.cs`
- `src/Cambrian.Application/DTOs/V1/LicensePurchaseRequest.cs`
- `tests/Cambrian.Api.Tests/V1/LicensesV1ControllerTests.cs`
- `contracts/openapi.v1.json`

## Deployment

Render deployment is controlled by `render.yaml`.

Known branch mapping:

- Staging deploys from `staging`.
- Production deploys from `main`.

Latest staging push from this workspace:

- Pushed `HEAD` to `origin/staging`.
- Remote changed from `2a6d297` to `49fc5cf`.
- Pre-push critical tests passed.
- Gitleaks scan was skipped because the local tool was missing.

Do not describe a push as deployed unless Render has actually built and promoted it. A Git push to `staging` is only the source-control side of deployment.

## Environment And Config

Configuration sources are appsettings, environment variables, and user secrets.

Important config areas:

- `ConnectionStrings:DefaultConnection` / `DATABASE_URL`
- `Jwt:Key`, `Jwt__Key`, `JWT_KEY`
- `Jwt:Issuer`, `Jwt:Audience`
- `Stripe:SecretKey`, `Stripe:WebhookSecret`
- `Storage:*`
- `Email:*`
- `Admin:Email`, `Admin:Password`
- `App:FrontendUrl`, `App:CorsOrigins`
- `Google:ClientId`

Production/staging secrets must not be committed.

## Agent Rules

- Read the actual source before changing behavior.
- Keep changes scoped to the user request.
- Do not silently bypass hooks or tests. If a hook is stale or wrong, say why before bypassing.
- Separate build failures, test failures, sandbox/tooling failures, and product regressions.
- If you touch contracts, run `node scripts/validate-contracts.cjs`.
- If you touch backend code, at minimum run a focused build/test that covers the changed area.
- If you touch payments, entitlements, auth, access control, or migrations, add or update tests unless the user explicitly forbids it.
- Do not claim staging or production runtime behavior without browser/API evidence from that environment.
