# Cambrian Backend Dead Code & Bloat Audit

Audit date: 2026-04-22

This audit was requested as a TypeScript/Node.js dead-code sweep, but the repository is primarily an ASP.NET Core 8 / C# backend with a thin Node.js tooling layer. I adapted the audit to the actual repo shape instead of forcing TS-only conclusions.

**Codebase Summary**

- Primary runtime: ASP.NET Core 8 Web API on .NET 8 (`README.md:3`, `README.md:37-45`, `src/Cambrian.Api/Cambrian.Api.csproj:1-25`).
- Entry point: `src/Cambrian.Api/Program.cs`, with runtime plus two CLI modes, `--generate` and `--seed` (`src/Cambrian.Api/Program.cs:25-43`).
- Database/ORM: PostgreSQL via Entity Framework Core (`README.md:39`, `src/Cambrian.Api/Program.cs:48-52`).
- Test framework: xUnit with `coverlet.collector`, but no coverage report is checked in or generated as part of this audit (`tests/Cambrian.Api.Tests/Cambrian.Api.Tests.csproj:10-19`).
- Node layer: tooling only. Root `package.json` contains six scripts, all for backend startup or contract tooling (`package.json:5-17`).
- CI is primarily `.NET restore/build/test`, plus two Node scripts for contract validation and breaking-change detection (`.github/workflows/ci.yml:29-34`, `.github/workflows/ci.yml:75-79`).
- There is no `tsconfig.json` anywhere in the repo, so `ts-prune` and `tsc --noEmit` cannot provide meaningful TypeScript unused-symbol coverage.

Approximate codebase size analyzed:

- ~150,495 LOC across `src/`, `tests/`, `scripts/`, `tools/`, `seed/`, and `loadtests/`
- 475 `.cs` files
- 16 `.js/.cjs/.mjs/.ts/.tsx` files

Tool output artifacts were saved to:

- `audit-artifacts/knip.json`
- `audit-artifacts/ts-prune.txt`
- `audit-artifacts/depcheck.json`
- `audit-artifacts/madge-circular-src.txt`
- `audit-artifacts/madge-orphans-src.txt`
- `audit-artifacts/madge-circular-js-surface.txt`
- `audit-artifacts/madge-orphans-js-surface.txt`
- `audit-artifacts/tsc-noemit.txt`
- `audit-artifacts/js-file-reference-map.json`
- `audit-artifacts/endpoint-frontend-usage.json`
- `audit-artifacts/interface-implementation-map.json`
- `audit-artifacts/todo-fixme.txt`
- `audit-artifacts/commented-code-suspects.txt`
- `audit-artifacts/skipped-tests.txt`
- `audit-artifacts/env-reference-map.json`

Tooling notes:

- `knip` was useful only for the small JS tooling surface. It flagged a cluster of orphaned maintenance scripts.
- `ts-prune` failed immediately because the repo has no `tsconfig.json`.
- `depcheck` found no unused root dependencies, but it did flag `@aws-sdk/client-s3` as missing for `migrate-storage.mjs`.
- `madge` on `src/` processed 0 files because `src/` is C#, not TS/JS. A supplemental scan over the JS tooling surface found no cycles and many orphan entrypoints.
- `tsc --noEmit` was not applicable for the same reason as `ts-prune`.

## 1. Executive Summary

- Total LOC analyzed: ~150,495
- Estimated deletable LOC:
  - High confidence: ~45 LOC
  - Medium confidence: ~2,700 LOC
  - Low confidence / structural cleanup: not estimated as direct deletions
- Top 5 biggest wins:
  - Delete the unused duplicate `SilentMp3Generator` in `src/Cambrian.Api/Tools/`
  - Remove inert growth-feature config members that have no callers
  - Retire the orphaned OpenAPI patch/reconciliation script cluster if no manual workflow still depends on it
  - Remove or archive placeholder API surfaces under `AiController` and the admin placeholders in `DataController` after confirming no outside consumers
  - Decide the fate of one-off operational scripts (`migrate-storage.mjs`, `scripts/email-pro-upgrade/*`) instead of letting them rot in the main repo
- Estimated cleanup effort:
  - Safe batch only: 1-2 hours
  - Medium-confidence verification plus cleanup PRs: 8-16 hours

## 2. High Confidence Deletions

| Path | Type | Size (LOC) | Evidence | Risk |
|------|------|-----------:|----------|------|
| `src/Cambrian.Api/Tools/SilentMp3Generator.cs` | `unused-file` | 36 | Duplicate implementation exists at `src/Cambrian.Infrastructure/Storage/SilentMp3Generator.cs:1-55`. The only live call site is `StreamController`, which imports `Cambrian.Infrastructure.Storage` and calls `SilentMp3Generator.Generate()` there (`src/Cambrian.Api/Controllers/StreamController.cs:3`, `src/Cambrian.Api/Controllers/StreamController.cs:133`). No external reference to `Cambrian.Api.Tools.SilentMp3Generator` was found. Last touched 2026-03-12 (`79cb047`). | Very low. Delete only the API copy. |
| `src/Cambrian.Infrastructure/Options/GrowthFeaturesOptions.cs:8-9`<br>`src/Cambrian.Infrastructure/FeatureFlags/ConfigurationFeatureFlagService.cs:23-24`<br>`src/Cambrian.Api/appsettings.json:62-63` | `unused-config` | 6 | `SalesTickerEnabled` and `CheckoutV2Enabled` appear only in these three locations. Repo-wide search found no controller, service, test, or workflow consuming either key. The config members are inert surface area. | Low. Remove the fields and matching switch arms together. |
| `src/Cambrian.Api/StartupExtensions.cs:206-209` | `commented-code` | 4 | This is a dead commented-out `TwilioSmsService` registration block. There is no `TwilioSmsService` implementation in the repo; active runtime registration is `ConsoleSmsService` at `src/Cambrian.Api/StartupExtensions.cs:211`. | None. |

## 3. Medium Confidence (Verify Before Delete)

| Path | Type | Size (LOC) | Evidence | Risk |
|------|------|-----------:|----------|------|
| `src/Cambrian.Api/Controllers/AiController.cs` | `dead-endpoint` | 19 | `GET /ai/playlist` returns `Array.Empty<object>()` (`src/Cambrian.Api/Controllers/AiController.cs:10-14`). `POST /generate` returns a fixed message (`src/Cambrian.Api/Controllers/AiController.cs:16-20`). These endpoints appear in backend manifests/contracts (`manifests/BACKEND_MANIFEST.json:113-114`, `contracts/API_CONTRACTS.md:1509-1512`) but not in `audit-artifacts/endpoint-frontend-usage.json`. No direct tests were found. Last touched 2026-03-06 (`ea18f97`, “Consolidate to 20 controllers matching OpenAPI contract”). | Medium. Confirm no mobile app, admin tool, or external partner calls these routes. |
| `src/Cambrian.Api/Controllers/DataController.cs:39-79` | `dead-endpoint` | 41 | The admin routes return placeholders only: `Array.Empty<object>()`, `{}`, or canned messages (`src/Cambrian.Api/Controllers/DataController.cs:39-79`). They are documented in the backend contract/manifests but not referenced by the frontend manifest (`audit-artifacts/endpoint-frontend-usage.json`). No dedicated tests were found. Last touched 2026-03-22 (`2b198f3`). | Medium. Verify no admin dashboard or operator scripts hit them. |
| `src/Cambrian.Api/Controllers/DataController.cs:21-37` | `dead-endpoint` | 17 | `GET /data/account` is more real than the other `DataController` methods, but it is still absent from the frontend manifest and lacks dedicated tests. It is only visible through contracts/manifests (`manifests/BACKEND_MANIFEST.json:143`, `contracts/API_CONTRACTS.md:1493`). | Medium. Verify no external or internal clients use it before removal. |
| `scripts/patch-openapi-contract.cjs`<br>`scripts/reconcile-contracts.cjs`<br>`scripts/generate-endpoint-manifest.cjs`<br>`scripts/fix-stale-openapi.cjs`<br>`scripts/extend-openapi.cjs`<br>`scripts/patch-openapi.cjs`<br>`scripts/patch-openapi-routes.cjs`<br>`scripts/patch-openapi-avatar.cjs` | `unused-file` | 2,053 | These scripts directly rewrite `contracts/openapi.v1.json` or related manifest artifacts (for example `scripts/patch-openapi-contract.cjs:1-25`, `scripts/reconcile-contracts.cjs:7-16`, `scripts/generate-endpoint-manifest.cjs:7-12`, `scripts/fix-stale-openapi.cjs:5-38`, `scripts/extend-openapi.cjs:14-15`). Hidden-file search found no references from `package.json`, CI, docs, or workflows. Current CI uses only `scripts/validate-contracts.cjs` and `scripts/detect-breaking-changes.cjs` (`.github/workflows/ci.yml:75-79`). Most were last touched between 2026-03-12 and 2026-03-29. | Medium. Confirm there is no undocumented manual contract-maintenance workflow before deleting them. |
| `migrate-storage.mjs` | `possibly-unused one-off script` | 105 | No external references were found beyond its self-documenting usage comment (`migrate-storage.mjs:8-9`). `depcheck` reports `@aws-sdk/client-s3` as missing for this file. The script contains hardcoded storage credentials/configuration in `migrate-storage.mjs:22-27`, which makes it risky to keep around if it is obsolete. Last touched 2026-04-11 (`7cf2de0`). | Medium. Verify the migration is complete, then delete or move it to an archived ops repo. Rotate/expire the embedded credentials if they are still valid. |
| `scripts/detect-email-leaks.cjs` | `possibly-unused one-off script` | 77 | This is a narrow creator-identity leak detector (`scripts/detect-email-leaks.cjs:1-13`). Hidden-file search found no references in root scripts, CI, workflows, or docs. Last touched 2026-03-22 (`b7d4935`). | Medium. Keep only if somebody still runs it manually during privacy reviews. |
| `scripts/email-pro-upgrade/send-pro-upgrade-email.mjs`<br>`scripts/email-pro-upgrade/list-recipients.mjs`<br>`scripts/email-pro-upgrade/package.json` | `possibly-unused one-off tooling` | 431 | The nested package explicitly calls itself a one-off “email all users letting them know their account was upgraded to Pro for life” tool (`scripts/email-pro-upgrade/package.json:5-13`). `send-pro-upgrade-email.mjs` is only referenced by that nested package’s local npm scripts (`scripts/email-pro-upgrade/package.json:6-9`); `list-recipients.mjs` has no external references. Hidden-file search found no root CI/workflow/docs usage. | Medium. Verify the campaign is complete and no reruns are expected before deleting or archiving this folder. |

Manual checks needed before deleting medium-confidence items:

1. Check whether any out-of-repo frontend/mobile/admin clients call `/ai/*` or `/data/*`.
2. Ask the owner whether the OpenAPI patch scripts are still part of any manual release process.
3. Confirm whether `migrate-storage.mjs` and `scripts/email-pro-upgrade/*` are historical one-offs or still operational runbooks.

## 4. Low Confidence (Investigate)

| Path | Type | Size (LOC) | Evidence | Risk |
|------|------|-----------:|----------|------|
| `src/Cambrian.Persistence/Migrations/20260323030051_SeedCreatorIdentityFeatureFlags.cs`<br>`src/Cambrian.Persistence/Migrations/20260419193928_SeedCheckoutV2FeatureFlag.cs`<br>`src/Cambrian.Api/Controllers/CreatorProfileController.cs:14,95`<br>`README.md:217-223`<br>`tests/Cambrian.Api.Tests/ContractTruthTests.cs:30` | `dead-flag-artifact` | 71 + docs/tests | `creator_profiles`, `username_routing`, and `checkout_v2` look partially abandoned. `creator_profiles` and `username_routing` exist in a seed migration (`20260323030051...:17-23`) and README/test references, but the actual storefront gate is `creator_storefront` (`src/Cambrian.Api/Controllers/CreatorProfileController.cs:95`). `checkout_v2` exists in its own seed migration (`20260419193928...:20-24`), while runtime config uses `CheckoutV2Enabled`, which itself has no callers. | High if deleted blindly. Do not remove migrations without a deliberate deprecation plan and data cleanup. |
| `src/Cambrian.Infrastructure/Options/SmsOptions.cs:8-15` | `possibly-unused-config` | 8 | Twilio-specific option fields exist, but there is no Twilio implementation and only a commented-out registration block in `src/Cambrian.Api/StartupExtensions.cs:206-209`. | Could be future work. Remove only if the team explicitly abandons Twilio. |
| `src/Cambrian.Infrastructure/Storage/R2ObjectStorage.cs:10-37` | `over-engineered abstraction` | 28 | This class is a placeholder, not production code: upload returns a synthetic URL, signed URLs are fake, reads return `null`, and delete is a no-op (`src/Cambrian.Infrastructure/Storage/R2ObjectStorage.cs:10-37`). | Not dead enough to delete without understanding storage roadmap. Better to either finish it or quarantine it as experimental. |
| `src/Cambrian.Application/Interfaces/*` + matching impls | `over-abstraction` | 54 interfaces | `audit-artifacts/interface-implementation-map.json` shows 54 interfaces total, 50 with exactly one implementation and only 4 with multiple implementations. This is classic layering bloat, but not dead code in the strict sense. | This is a refactor decision, not a safe deletion. |
| `src/Cambrian.Application/Services/CatalogService.cs:143-168`<br>`src/Cambrian.Application/Services/CreatorService.cs:59-83`<br>`src/Cambrian.Application/Services/StorefrontService.cs:118-133`<br>`src/Cambrian.Persistence/Repositories/CreatorIdentityRepository.cs:112,187` | `duplicate-logic` | ~20 duplicated lines | The same price alias/fallback normalization appears in several services/repositories. This is not dead code, but it is duplicated logic that will drift. | Low immediate risk, but a good consolidation target. |
| `tests/Cambrian.Api.Tests/StabilityTests.cs:323,456,479,503,527` | `stale-skipped-test` | 5 test cases | Five tests are skipped with messages saying validation was reverted and should be restored later (`tests/Cambrian.Api.Tests/StabilityTests.cs:323-336`, `456-500`, `503-527`). Git blame shows these skips were added recently, on 2026-04-09, so this is not “ancient” dead code, but it is disabled coverage worth tracking. | Low. Re-enable rather than delete. |

## 5. Dependency Cleanup

Unused dependencies to remove:

- None at the root package. `depcheck` reported no unused root dependencies.

Missing dependencies to add:

- If `migrate-storage.mjs` is kept, add:

```bash
npm install -D @aws-sdk/client-s3
```

- Do **not** add `k6` to `package.json`. `depcheck` flags it because the load-test scripts import it, but this repo treats `k6` as an external CLI, not an npm dependency. That is consistent with:
  - `loadtests/README.md:5-18`
  - `.github/workflows/resilience-tests.yml:19-25`

Outdated major versions worth upgrading:

- Root `package.json`:
  - `openapi-to-postmanv2` is pinned at `^4.23.0` (`package.json:14-16`), while npm shows `5.1.0` as the latest package version as of 2026-04-22.
  - `yaml` is pinned at `^1.10.2` (`package.json:14-16`), while npm shows `2.8.1` as the latest package version as of 2026-04-22.
- Nested one-off package `scripts/email-pro-upgrade/package.json`:
  - `resend` is pinned at `^4.0.1` (`scripts/email-pro-upgrade/package.json:11-13`), while npm shows `6.0.2` as latest as of 2026-04-22.
  - `pg` is pinned at `^8.13.1` (`scripts/email-pro-upgrade/package.json:11-13`), while npm shows `8.16.0`; that is not a major-version gap.

Recommendation:

- Upgrade root tooling deps only if you plan to keep and maintain the Node tooling surface.
- Do not spend time upgrading `scripts/email-pro-upgrade/` unless that package survives the cleanup review.

## 6. Architectural Observations

- The repo is much more C#-heavy than the request implied. The Node layer is contract tooling and one-off ops scripts, not the main runtime.
- `madge` found no JS circular dependencies. The initial `src/` scan was empty because `src/` is C#; the supplemental JS scan also found no cycles.
- Placeholder API surface appears to be carrying contract shape rather than product behavior:
  - `AiController` is entirely placeholder.
  - Most of `DataController` is placeholder.
- `Program.cs` still contains a temporary `--generate` controller-stub path (`src/Cambrian.Api/Program.cs:27-33`), while `Cambrian.Api.csproj` excludes `GeneratedControllers/**` from compilation (`src/Cambrian.Api/Cambrian.Api.csproj:28-31`). That is a sign of contract/codegen scaffolding that can drift quietly.
- Interface layering is heavy for the size of the codebase:
  - 50 of 54 interfaces have exactly one implementation.
  - The only interfaces with real polymorphism are `IWebhookService`, `IEmailService`, `IPaymentGateway`, and `IObjectStorage`.
- `R2ObjectStorage` is an incomplete abstraction presented as a real implementation candidate. That increases surface area without delivering behavior.

## 7. Cleanup Plan (Prioritized)

1. Batch 1: Safe, surgical cleanup
   - Delete `src/Cambrian.Api/Tools/SilentMp3Generator.cs`
   - Remove `SalesTickerEnabled` and `CheckoutV2Enabled` from options, switch mapping, and `appsettings.json`
   - Delete the commented Twilio registration block
   - Run full tests and type/build checks

2. Batch 2: Orphan tooling review
   - Review the OpenAPI patch/reconciliation scripts as one PR
   - If the owner confirms there is no manual workflow using them, delete them together
   - Keep `scripts/validate-contracts.cjs` and `scripts/detect-breaking-changes.cjs`; they are live CI inputs

3. Batch 3: Placeholder endpoint decision
   - Confirm whether `/ai/*` and `/data/*` have any external consumers
   - If not, remove controllers and clean the related OpenAPI/manifests/contracts in the same PR
   - If they must stay for compatibility, mark them explicitly as deprecated placeholders in code and contract docs

4. Batch 4: One-off ops tooling review
   - Decide whether `migrate-storage.mjs` belongs in the repo at all
   - Decide whether `scripts/email-pro-upgrade/` is historical archive material
   - Delete or move confirmed one-off scripts to an `ops-archive/` location outside the normal development path

5. Batch 5: Structural follow-up
   - Deprecate or reconcile dead feature-flag names
   - Decide whether Twilio support is truly on the roadmap
   - Either implement or quarantine `R2ObjectStorage`
   - Consolidate duplicated price normalization logic

## 8. What I Couldn't Assess

- Production traffic and endpoint request counts were not available, so route-level deadness for `/ai/*`, `/data/*`, and some admin routes cannot be proven from runtime evidence.
- APM and query logs were not available, so I could not prove unused tables, columns, or indexes from real traffic.
- External consumers are unknown. This repo alone cannot tell us whether a mobile app, admin tool, Zapier flow, or partner integration still calls these routes.
- Dynamic C# behaviors are conservative by necessity. DI, reflection, and manual operator workflows are harder to prove than static JS imports.
- Exact test coverage percentage was not assessed. The test project references `coverlet.collector`, but no coverage artifact was produced in this audit.
- TypeScript-specific dead-code checks were limited because the repo has no `tsconfig.json` and effectively no TypeScript project to analyze.

## Safety Checks Before Deletion PRs

1. Run the full test suite on the cleanup branch.
2. Run a full build and type/build validation (`dotnet build`, `dotnet test`, contract validation scripts).
3. Deploy the cleanup branch to staging and run smoke tests.
4. Check for external services, scripts, cron jobs, or operator runbooks that may still depend on removed code.
5. Keep deletions in small, reviewable PRs instead of one giant “dead code removal” change.

## External Version Sources

- `openapi-to-postmanv2`: https://www.npmjs.com/package/openapi-to-postmanv2
- `yaml`: https://www.npmjs.com/package/yaml
- `resend`: https://www.npmjs.com/package/resend
- `pg`: https://www.npmjs.com/package/pg
