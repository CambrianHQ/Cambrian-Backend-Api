# Cambrian Backend Security Baseline

Last updated: 2026-06-30

## Launch Status

Current backend security status: **NOT READY**

The backend has meaningful controls in place, including JWT/cookie auth, email-verification gates, capability policies, owner checks, Stripe webhook signature verification, webhook idempotency, CORS allowlisting, upload validation, static upload blocking, and rate limiting. It is not launch-ready until the full suite and production configuration checks pass on the deploy candidate and the remaining risks in `SECURITY_REMEDIATION_REPORT.md` are closed or accepted.

## Trust Boundaries

| Boundary | Trusted side | Untrusted side | Main controls |
| --- | --- | --- | --- |
| Browser/client to API | ASP.NET Core API | Browser, mobile clients, scripts | JWT bearer or HttpOnly cookie auth, CSRF middleware for cookie writes, CORS allowlist, rate limits |
| API to PostgreSQL | Application services/repositories | HTTP callers | EF Core repositories, authz checks before sensitive queries, migrations fail startup on stale schema |
| API to object storage | `IObjectStorage` implementations | Uploaded files, storage keys, public media consumers | provider validation, upload validators, static audio blocking, signed/proxied audio paths |
| API to Stripe platform | Billing/webhook services | Client checkout requests, Stripe events | server-side price IDs, checkout kill switch, webhook signature verification, event ledger/idempotency |
| API to Stripe Connect | Connect webhook/service layer | Creator payout and monetization events | separate Connect webhook secret, owner checks, Connect status checks |
| API to email provider | Email service/webhook | Resend/Svix webhook caller | Svix signature verification, timestamp tolerance, configured secret required |
| API/MCP public data | MCP/API-key endpoints | AI clients and public consumers | API-key integration policy for MCP transport, public DTOs, per-key/IP rate limiting |
| Admin/control plane | Admin controllers and diagnostics | Non-admin users | `[Authorize(Roles = "Admin")]`, default interactive-user policy, no API-key access to generic `[Authorize]` endpoints |

## Endpoint Classes

Public endpoints are intentionally limited to liveness, auth bootstrap, selected catalog/discovery reads, public profile/storefront reads, provenance verification, public v1 catalog reads, public charts/plans, anonymous stream audio for public tracks, and provider webhooks that verify signatures.

Authenticated endpoints include `/auth/me`, account settings, library, invoices, billing portal/status, downloads, release-ready jobs, stream session start/stop, track provenance details, feature flag checks, and user profile writes.

Creator endpoints require authentication plus creator-tier/username checks where applicable. Upload, edit/delete track, payout, creator profile media, release-ready credit checkout, and creator catalog management must remain server-side gated.

Admin endpoints live under `/admin`, `/debug`, admin health diagnostics, admin feature flag writes, data diagnostics, entitlement grant/revoke, and QA preflight. They must require Admin role and must not accept API-key authentication.

Payment endpoints that can create charges are behind auth and the checkout kill switch. Entitlements must be granted only through webhook-verified and idempotent fulfillment paths.

## Sensitive Data Rules

Public DTOs and MCP/API responses must not expose emails, Stripe customer/account/session IDs, payment details, raw storage keys, signed URLs, admin flags, unpublished/private/draft/hidden tracks, or private creator financial data.

Allowed public data includes public track metadata, creator display/profile data, public stats intentionally exposed by settings, public provenance verification material, canonical URLs where available, and public catalog/search fields.

## Security Controls

- Authentication: Identity password policy, lockout, JWT bearer/cookie transport, `auth_token` cookie validation, `/auth/me` protected, password reset endpoints rate-limited.
- Authorization: default `[Authorize]` policy is interactive-user-only; API keys require explicit `AllowApiKey` plus API-key integration policy; capability policies gate upload/edit/delete/payout/license paths.
- CORS: production origins are explicit; wildcard credentials are not configured.
- Rate limiting: global, auth, community, MCP, and API-key public v1 policies are registered.
- Headers: HSTS outside development, `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy`, and media-specific `Cross-Origin-Resource-Policy`.
- Error handling/logging: centralized exception middleware and request logging; webhook failures return retryable 500 for Stripe processing errors.
- Upload/storage: static file middleware blocks direct uploaded audio access; object storage is required in Production.
- Webhooks: Stripe platform, Stripe Connect, and Resend webhooks require configured secrets and signature verification.
- Contracts: `contracts/endpoint-manifest.v1.json` and `/manifest.json` now derive protected/public status from controller or endpoint authorization metadata.

## Validation Commands

Run before any launch claim:

```powershell
dotnet test Cambrian.sln --configuration Release --no-restore
node scripts/validate-contracts.cjs
node scripts/check-contract-drift.cjs
node scripts/detect-breaking-changes.cjs contracts/openapi.generated.json
npm audit --offline --json
```

If a command cannot run because of sandbox, network, Docker, or NuGet configuration, report it as blocked. Do not substitute a passing focused test for a launch-ready verdict.

## Security Reporting

Report suspected vulnerabilities to the project owner through the private repository issue tracker or the agreed internal security channel. Include endpoint, environment, request/response evidence with secrets redacted, and whether the action was read-only or mutating.
