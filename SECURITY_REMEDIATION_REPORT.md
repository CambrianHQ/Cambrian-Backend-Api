# Cambrian Backend Security Remediation Report

Date: 2026-06-30

## Executive Summary

Security status: **NOT READY**

Security score: **78/100**

Top remaining risks:

- Contract drift and live production-mode verification are not complete in this sprint pass.
- OpenAPI generated contract security semantics still need a full generation/diff pass after manifest hardening; the existing drift checker reports unrelated pre-existing response/status drift.
- Production environment values have not been verified live with secrets redacted.
- Malware scanning for uploaded media is documented as a plan, not implemented.
- Rate-limit behavior still needs verification behind the real deployment proxy/client-IP behavior.

What changed in this pass:

- Fixed `GET /manifest.json` so it derives auth/role/policy truth from ASP.NET endpoint metadata rather than Swagger operation security.
- Replaced the checked-in endpoint-manifest generator with a controller-attribute-aware generator.
- Regenerated `contracts/endpoint-manifest.v1.json`; protected routes are no longer advertised as public.
- Added regression tests proving both checked-in and runtime manifests do not mark protected routes public.
- Added repo-root `SECURITY.md`, `THREAT_MODEL.md`, and `PRODUCTION_SECURITY_CHECKLIST.md`.
- Ran the full backend suite and dependency audits.

## Findings

| Severity | Affected endpoint/surface | Root cause | Fix | Tests | Residual risk |
| --- | --- | --- | --- | --- | --- |
| High | `contracts/endpoint-manifest.v1.json`, `/manifest.json` | Manifest auth status came from stale hard-coded path inference or Swagger operation security, not endpoint metadata | Added `EndpointManifestFactory`, updated runtime `/manifest.json`, rewrote `scripts/generate-endpoint-manifest.cjs`, regenerated manifest | `EndpointManifestSecurityTests` passed 2/2 | OpenAPI generated contract still needs full validation |
| Medium | Security baseline docs | Required repo-root security docs were missing | Added `SECURITY.md`, `THREAT_MODEL.md`, `PRODUCTION_SECURITY_CHECKLIST.md` | Document review by source inspection | Docs must be kept current with future endpoint changes |
| Medium | Production readiness evidence | Live config and contract drift checks are not complete in this pass | Checklist and report now block READY status until evidence exists | Full backend suite and dependency audits passed | Backend remains NOT READY |
| Medium | Upload malware scanning | No antivirus/malware scanning implementation found | Documented as launch risk | Existing upload validation tests not rerun in this pass | Must implement scanning or explicitly accept risk before public launch |

## Security Matrix

| Area | Status | Tests | Remaining risk |
| --- | --- | --- | --- |
| Authentication | Covered by current suite | Full suite passed | Live production-mode/browser verification pending |
| Authorization | Improved | Manifest auth tests and full suite passed | Live role/account fixtures not verified |
| Public API/MCP | Covered by current suite | Full suite passed | Public DTO leak review should be repeated after new fields |
| Creator data | Covered by current suite | Full suite passed | Live owner-account smoke pending |
| Uploads | Covered by current suite | Full suite passed | Malware scanning not implemented |
| Streaming | Covered by current suite | Full suite passed | Real abuse/rate-limit behavior pending |
| Payments | Covered by current suite | Full suite passed | Live Stripe dashboard/config not verified |
| Stripe webhooks | Covered by current suite | Full suite passed | Live webhook endpoint secrets not verified |
| Stripe Connect | Covered by current suite | Full suite passed | Live Connect status/config not verified |
| Entitlements | Covered by current suite | Full suite passed | Live cancellation/refund smoke pending |
| Rate limiting | Partial | Policies configured | Real proxy/IP behavior pending |
| CORS | Partial | Explicit origins configured | Deploy smoke pending |
| Security headers | Partial | Middleware configured | Deploy smoke pending |
| Logging/errors | Partial | Middleware/services present | Production response/log review pending |
| OpenAPI/contracts | Improved | Manifest tests and contract validation passed | `check-contract-drift.cjs` still reports pre-existing drift |
| Production config | Partial | Checklist added | Live redacted verification pending |

## Sensitive Data Exposure Matrix

| Data type | Public exposure allowed? | Endpoint checked | Result |
| --- | --- | --- | --- |
| Emails | No | Public API/MCP surfaces | Pending full leak tests |
| Stripe customer/account/session IDs | No | Billing, Connect, public DTOs | Pending full leak tests |
| Payment amounts/status | Owner/admin only | Billing, invoices, payouts | Pending full owner/admin tests |
| Raw storage keys | No | Catalog, stream, profile media | Pending full leak tests |
| Signed URLs | Owner/authorized flow only | Download/stream signed paths | Pending full stream/download tests |
| Private/draft/hidden tracks | No | Catalog, creator tracks, stream audio | Pending full visibility tests |
| Admin diagnostics | Admin only | `/admin`, `/debug`, health diagnostics | Manifest now marks admin/protected; runtime auth still must be fully tested |
| Public track/profile metadata | Yes | Catalog/profile/v1 endpoints | Allowed, subject to DTO leak tests |
| Provenance verification data | Yes | `/api/provenance/*`, `/verify/{recordId}` | Allowed public verification data |

## Payment Security Matrix

| Flow | Signature verified | Idempotent | Entitlement safe | Tests | Status |
| --- | --- | --- | --- | --- | --- |
| Creator/Pro checkout | Webhook required | Event ledger/session checks | Webhook grants paid state | Full suite passed | Local pass |
| Release Ready credit packs | Webhook required | Session/pack checks | Credits granted after verified payment | Full suite passed | Local pass |
| Authorship Record | Webhook required | Session/record checks | Record issued after verified payment | Full suite passed | Local pass |
| Billing portal | Not a webhook flow | N/A | Requires auth | Full suite passed | Local pass |
| Refund/dispute/failed payment | Stripe event required | Event ledger | Revocation/state behavior defined in service | Full suite passed | Local pass |
| Stripe Connect tips | Connect webhook secret | Connect event ledger | Creator ownership/status checked | Full suite passed | Local pass |
| Fan subscriptions | Connect webhook secret | Connect event ledger | Creator ownership/status checked | Full suite passed | Local pass |

## Production Config Checklist

Values must be verified in the deployment dashboard with secrets redacted:

| Key | Status |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | Required, must be `Production` |
| `DATABASE_URL` / `ConnectionStrings__DefaultConnection` | Required, REDACTED |
| `Jwt__Key`, `Jwt__Issuer`, `Jwt__Audience` | Required, key REDACTED |
| `Stripe__SecretKey`, `Stripe__WebhookSecret`, `Stripe__ConnectWebhookSecret` | Required, REDACTED |
| `Stripe__Prices__Creator`, `Stripe__Prices__Pro` | Required, REDACTED |
| `Checkout__Enabled` | Required launch decision; currently false in `render.yaml` |
| `Storage__Provider`, `Endpoint`, `Bucket`, `AccessKey`, `SecretKey`, `Region`, `UsePathStyle`, `PublicUrl` | Required, secrets REDACTED |
| `Email__Provider`, `FromAddress`, `FromName`, `ResendApiKey`, `ResendWebhookSecret` | Required for Resend, secrets REDACTED |
| `App__FrontendUrl`, `App__CorsOrigins` | Required, explicit production origins |
| `ForwardedHeaders__KnownProxies` / `KnownNetworks` | Optional; empty means do not trust forwarded headers |
| `Provenance__Signing__PrivateKeyPem` | Required in Production, REDACTED |
| `Sentry__Dsn` | Recommended, REDACTED if configured |
| `Google__ClientId` | Required only if Google OAuth is enabled |
| `Admin__Email`, `Admin__Password` | Operational bootstrap, REDACTED |

## Validation Evidence

Passed:

```powershell
dotnet test tests\Cambrian.Api.Tests\Cambrian.Api.Tests.csproj --configuration Release --no-restore --filter EndpointManifestSecurityTests
dotnet test Cambrian.sln --configuration Release --no-restore
node scripts\validate-contracts.cjs
node scripts\detect-breaking-changes.cjs contracts\openapi.generated.json
npm audit --offline --json
dotnet list Cambrian.sln package --vulnerable --include-transitive --configfile NuGet.Config
```

Results:

- Focused manifest suite: 2 passed, 0 failed, 0 skipped.
- Full backend suite: 941 passed, 0 failed, 4 skipped.
- `validate-contracts.cjs`: exit 0 with existing non-blocking architecture warnings.
- `detect-breaking-changes.cjs`: no breaking changes detected against local `contracts/openapi.generated.json` (216 baseline paths, 216 generated paths).
- `npm audit --offline --json`: 0 vulnerabilities.
- NuGet vulnerability audit: no vulnerable packages across all projects.
- Secret scan: only local/test placeholders and documentation examples matched.

Blocked/remaining:

- The same focused test without `--no-restore` failed before test execution because the sandbox could not read `C:\Users\logan\AppData\Roaming\NuGet\NuGet.Config`.
- `node scripts\check-contract-drift.cjs` fails on pre-existing status/envelope/path drift unrelated to the endpoint-manifest fix.
- `git diff --check` fails on unrelated dirty files that predated this pass; whitespace is clean for files touched in this pass.
- Live production smoke/config verification is still pending.
