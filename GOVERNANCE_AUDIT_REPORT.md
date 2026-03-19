# Governance Audit Report

> Generated: 2026-03-19 | Contract version: 2.0.0

---

## 1. Mismatches Fixed

### Endpoint Manifest Corrections (contracts/endpoint-manifest.v1.json)

| Endpoint | Field | Was | Now |
|----------|-------|-----|-----|
| `GET /creator/tracks` | tag | `Ai` | `Creator` |
| `GET /creator/revenue` | tag | `Ai` | `Creator` |
| `GET /discover` | requiresAuth | `true` | `false` |
| `GET /catalog` | requiresAuth | `true` | `false` |
| `GET /trending` | requiresAuth | `true` | `false` |
| `GET /tracks/{trackId}` | requiresAuth | `true` | `false` |
| `GET /community` | requiresAuth | `true` | `false` |

### .env.example Gaps Fixed

| Variable | Issue |
|----------|-------|
| `Stripe__SecretKey` | Was `STRIPE_SECRET_KEY` (wrong binding format) |
| `Stripe__WebhookSecret` | Was `STRIPE_WEBHOOK_SECRET` (wrong binding format) |
| `Storage__Region` | Missing |
| `Storage__PublicUrl` | Missing |
| `Email__FromAddress` | Missing |
| `Email__FromName` | Missing |
| `Email__ResendApiKey` | Missing |
| `Admin__Email` | Missing |
| `Admin__Password` | Missing |

### Governance Files Added

| File | Purpose |
|------|---------|
| `architecture/ARCHITECTURE.md` | System design, data flow, entity model |
| `contracts/API_CONTRACTS.md` | Canonical endpoint shapes (request/response) |
| `policy/POLICY.md` | Engineering rules, change management process |
| `manifests/BACKEND_MANIFEST.json` | 130+ endpoints, 17 tables, 28 env vars, 24 services |
| `manifests/FRONTEND_MANIFEST.json` | Routes, API dependencies, components, env vars |
| `governance/SOURCE_OF_TRUTH.md` | Priority hierarchy and master file index |
| `governance/AI_GOVERNANCE.md` | AI agent rules |

### Startup Logging Enhanced

- Added applied migration count and latest migration name to startup log
- Added governance contract version to startup log

---

## 2. Remaining Risks

### HIGH: Pre-existing Test Build Failures

4 test files have constructor signature mismatches with `CheckoutService` (missing `ILogger<CheckoutService>` parameter):
- `CheckoutTests.cs:34`
- `CopyrightBuyoutTests.cs:65`
- `CheckoutServiceTests.cs:31`
- `PurchaseJourneyTests.cs:86`

**Impact:** Tests cannot run. CI/CD pipeline is broken for test validation.
**Fix:** Add `NullLogger<CheckoutService>.Instance` as the last constructor argument in each test.

### MEDIUM: TrackResponse Missing Fields for Frontend

The `TrackResponse` DTO does not include:
- `mood` — present in entity, absent from DTO (filtering works but field not returned)
- `tempo` — present in entity, absent from DTO
- `instrumental` — present in entity, absent from DTO
- `tags` — present in entity, absent from DTO
- `visibility` — present in entity, absent from DTO

**Impact:** Frontend cannot display these metadata fields even though they're filterable.
**Fix:** Add nullable fields to `TrackResponse` DTO and update `contracts/API_CONTRACTS.md`.

### MEDIUM: Identity Field Ambiguity

The `TrackResponse.Artist` field maps to `DisplayName` which is correct, but there's no `creatorSlug` or `creatorProfileUrl` field to link to the creator's storefront from a track card.

**Impact:** Frontend must make a separate API call to resolve creator profile from `creatorId`.
**Fix (future):** Add optional `creatorSlug` and `creatorProfileImageUrl` to `TrackResponse`.

### LOW: Dual Payment Endpoints

Both `/checkout` and `/payments/checkout` create Stripe sessions. Also `/billing/checkout` and `/billing/checkout-session`.

**Impact:** Frontend may call either, creating confusion.
**Fix:** Document which is primary in `contracts/API_CONTRACTS.md` (done), deprecate duplicates over time.

### LOW: ApplicationUser.Tier vs CreatorTier Redundancy

`ApplicationUser` has both `Tier` (string: "free", "paid", "creator", "pro") and `CreatorTier` (enum: Free, Pro). These overlap and can drift.

**Impact:** Fee calculation could be wrong if these desync.
**Fix:** `CreatorTier` is canonical for fee calculation. `Tier` is informational/legacy. Document this in `architecture/ARCHITECTURE.md` (done).

### LOW: Missing Endpoints in Legacy Manifest

The `contracts/endpoint-manifest.v1.json` is missing ~15 endpoints added after its generation date (2026-03-13), including:
- `POST /admin/purge-test-data`
- `POST /billing/checkout-session`
- Debug endpoints
- Some analytics endpoints

**Impact:** Contract validation script may miss undocumented endpoints.
**Fix:** The new `manifests/BACKEND_MANIFEST.json` has the complete list. Legacy manifest should be regenerated.

---

## 3. Enforcement Rules Summary

| Rule | Enforcement | Where |
|------|------------|-------|
| No API shape changes without contract update | PR review | `contracts/API_CONTRACTS.md` |
| No env var changes without .env.example update | PR review | `config/.env.example` |
| No destructive DB migrations | Code review | `policy/POLICY.md` Section 3 |
| Email never used as public identity | Code review + startup | `policy/POLICY.md` Section 4 |
| Username/slug is only public creator ID | DTO audit | `policy/POLICY.md` Section 4 |
| Frontend types match backend DTOs | Type generation/review | `policy/POLICY.md` Section 5 |
| No hardcoded API URLs | Code review | `policy/POLICY.md` Section 6 |
| Required env vars validated on startup | Runtime crash | `StartupExtensions.cs` |
| Database connection logged (safe info) | Runtime log | `StartupExtensions.cs` |
| Schema compatibility logged | Runtime log | Migration count + latest |
| Contract version logged | Runtime log | `Program.cs` startup |
| JWT key ≥ 32 chars | Runtime crash | All environments |
| Production: no local storage | Runtime crash | `StartupExtensions.cs` |
| Production: no console email | Runtime crash | `StartupExtensions.cs` |
| Production: no test Stripe keys | Runtime crash | `StartupExtensions.cs` |

---

## 4. How to Maintain Alignment Going Forward

### Before Every PR

1. **DTO change?** → Update `contracts/API_CONTRACTS.md` with new shape
2. **New endpoint?** → Add to `contracts/API_CONTRACTS.md`, `manifests/BACKEND_MANIFEST.json`
3. **New env var?** → Add to `config/.env.example`, `manifests/BACKEND_MANIFEST.json`, `render.yaml`
4. **DB migration?** → Update `manifests/BACKEND_MANIFEST.json` (tables section)
5. **Frontend route change?** → Update `manifests/FRONTEND_MANIFEST.json`
6. **New service?** → Update `manifests/BACKEND_MANIFEST.json` (services section)

### Validation Commands

```bash
# Build (must pass with 0 errors)
dotnet build src/Cambrian.Api/Cambrian.Api.csproj

# Contract validation
node scripts/validate-contracts.cjs

# Pre-deploy tests
pwsh scripts/pre-deploy-tests.ps1

# Full verification
pwsh verify.ps1
```

### Governance File Locations

| File | Purpose | When to Update |
|------|---------|---------------|
| [architecture/ARCHITECTURE.md](architecture/ARCHITECTURE.md) | System design | Entity or infrastructure changes |
| [contracts/API_CONTRACTS.md](contracts/API_CONTRACTS.md) | Endpoint shapes | Any DTO or endpoint change |
| [policy/POLICY.md](policy/POLICY.md) | Engineering rules | Policy or process changes |
| [manifests/BACKEND_MANIFEST.json](manifests/BACKEND_MANIFEST.json) | Backend inventory | Any backend change |
| [manifests/FRONTEND_MANIFEST.json](manifests/FRONTEND_MANIFEST.json) | Frontend inventory | Any frontend change |
| [config/.env.example](config/.env.example) | Env var structure | Any config change |
| [governance/backend-policy.v1.json](governance/backend-policy.v1.json) | Machine-readable policy | Architecture rule changes |
| [governance/SOURCE_OF_TRUTH.md](governance/SOURCE_OF_TRUTH.md) | Priority hierarchy | Structure changes |
| [governance/AI_GOVERNANCE.md](governance/AI_GOVERNANCE.md) | AI agent rules | AI workflow changes |
| [contracts/policy.v1.json](contracts/policy.v1.json) | Compliance policy | Compliance rule changes |
| [contracts/endpoint-manifest.v1.json](contracts/endpoint-manifest.v1.json) | Legacy endpoint list | Regenerate on major changes |
| [contracts/openapi.v1.json](contracts/openapi.v1.json) | OpenAPI spec | Endpoint changes |
