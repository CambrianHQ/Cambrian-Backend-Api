# Security regression test report

Date: 2026-06-18

Verdict: **BACKEND PATCH PASS / PRODUCTION NO-GO**

## Focused security suite

Command:

```powershell
dotnet test tests/Cambrian.Api.Tests/Cambrian.Api.Tests.csproj `
  --configuration Release --no-build `
  --filter "FullyQualifiedName~ApiKeyIsolationTests|FullyQualifiedName~CreatorImageGrantTests|FullyQualifiedName~CookieCsrfTests|FullyQualifiedName~ForwardedHeaderAndLockoutTests|FullyQualifiedName~MetadataAndEmailInjectionTests|FullyQualifiedName~PublicDiagnosticsSecurityTests|FullyQualifiedName~QaPreflightTests" `
  -v minimal
```

Result: **32 passed, 0 failed, 0 skipped**

Coverage:

- API key cannot call `/auth/me`.
- API key cannot call `/api/v1/keys`.
- API key cannot call settings or billing.
- API key cannot mutate profile or upload.
- API key cannot access payouts.
- JWT users retain legitimate account access.
- API key can call explicitly marked V1 integration routes.
- MCP rejects unauthenticated requests.
- Creator A cannot consume creator B avatar or cover grants.
- Listener cannot request creator-image grants.
- Invalid prefix and traversal are rejected.
- Missing-length/chunked oversized upload is rejected.
- Invalid MIME and invalid magic bytes are rejected.
- Valid owner upload succeeds once; replay fails.
- Cookie mutation without CSRF fails.
- Invalid CSRF fails; valid CSRF succeeds.
- Bearer mutation remains valid without CSRF.
- Multipart cookie mutation is protected.
- Forged `X-Forwarded-For` does not change the rate-limit key.
- Forwarded-header trust requires explicit configuration.
- Five failed logins lock only the target account.
- Stored metadata rejects the exact script-breakout payload.
- Email title/display-name injection is encoded.
- Unsafe email URLs are rejected.
- Public health is minimal.
- QA preflight is not public.

Test file:

- `tests/Cambrian.Api.Tests/Security/PentestSecurityRegressionTests.cs`

## Build

Command:

```powershell
dotnet build Cambrian.sln --configuration Release --no-restore -m:1
```

Result: **PASS, 0 warnings, 0 errors**

## Broad backend suite

Command:

```powershell
dotnet test Cambrian.sln --configuration Release --no-build -m:1 `
  --filter "FullyQualifiedName!~ReleaseReadyCreditTests.ConcurrentCharges_WithOneCreditLeft_ExactlyOneSucceeds" `
  -v minimal
```

Result: **922 passed, 4 skipped, 0 failed**

Known unrelated test-harness failure:

- `ReleaseReadyCreditTests.ConcurrentCharges_WithOneCreditLeft_ExactlyOneSucceeds`
- Fails on the shared in-memory SQLite connection with
  `cannot start a transaction within a transaction`.
- The test is intended to prove concurrent serializable transactions, which a
  single shared SQLite connection cannot model. No pentest remediation code is
  in that stack.

## Contract validation

Command:

```powershell
npm run validate:contracts
```

Result: **PASS with five pre-existing non-blocking architecture warnings**

The new `/health/details` route was added to OpenAPI and the endpoint manifest
was regenerated.

## Dependency audits

Commands:

```powershell
npm audit --json
dotnet list Cambrian.sln package --vulnerable --include-transitive
```

Results:

- npm: **0 vulnerabilities**
- NuGet: **no vulnerable packages**

## Tooling verification

Commands:

```powershell
npm run export:postman
node --check tools/export-postman/export-postman.mjs
node --check scripts/patch-openapi-contract.cjs
```

Result: **PASS**

## Source scans

Results:

- Active backend docs/config contain no `api.cambrianmusic.com`.
- OpenAPI contains no obsolete public V1 licensing routes.
- Backend repository contains no JSON-LD `dangerouslySetInnerHTML` sink.
- Frontend JWT/localStorage and CSP findings are documented in
  `docs/security/JWT_SESSION_MIGRATION_PLAN.md`.
- The creator-image catch-all route remains only as the consumer of an exact,
  one-time, user-bound grant; arbitrary caller-selected keys are rejected.

## Frontend tests

Not run. The frontend repository was not in this workspace.

Required before production GO:

- Frontend build.
- Safe JSON-LD unit/integration test.
- Playwright proof that the injected script does not execute.
- Rendered-HTML regression scan.
- CSP nonce/hash migration validation.
- JWT localStorage migration validation.

## Live-safe smoke

Target:

`https://cambrian-backend-api.onrender.com`

Result:

- Correct target confirmed.
- Public catalogue and OpenAPI returned 200.
- Current deployment still exposes detailed health and preflight responses.
- Therefore the live environment is **NO-GO** until the reviewed backend patch
  and the frontend CAM-PENTEST-001 fix are deployed and re-tested.
