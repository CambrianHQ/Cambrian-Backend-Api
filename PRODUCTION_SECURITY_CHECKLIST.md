# Cambrian Production Security Checklist

Last updated: 2026-06-30

Current verdict: **NO-GO**

Do not mark production READY until every required gate below is either passing with evidence or explicitly accepted by the project owner as residual risk.

## Launch Gates

| Gate | Status | Evidence required |
| --- | --- | --- |
| Full backend test suite | Passed locally | `dotnet test Cambrian.sln --configuration Release --no-restore` passed 941/945 with 4 skipped |
| Contract validation | Passed with warnings | `node scripts/validate-contracts.cjs` exited 0 with existing non-blocking architecture warnings |
| Contract drift validation | Failing | `node scripts/check-contract-drift.cjs` reports pre-existing status/envelope/path drift |
| OpenAPI breaking-change validation | Passed locally | `node scripts/detect-breaking-changes.cjs contracts/openapi.generated.json` reported no breaking changes |
| Dependency audit | Passed locally | NuGet vulnerability audit and `npm audit --offline --json` found 0 vulnerabilities |
| Secret scan | Passed lightweight scan | Only local/test placeholders and docs examples matched |
| Production env validation | Pending | Required values verified in Render/dashboard, values redacted |
| Live smoke/security check | Pending | Production-safe read-only smoke on deployed candidate |

## Required Production Environment

Values must be present in the deployment environment. Store actual values only in the secret manager/dashboard.

| Key | Required | Value status | Security notes |
| --- | --- | --- | --- |
| `ASPNETCORE_ENVIRONMENT` | Yes | `Production` | Must not be `Development` or `Testing` |
| `DATABASE_URL` or `ConnectionStrings__DefaultConnection` | Yes | REDACTED | PostgreSQL; no committed credentials |
| `Jwt__Key` | Yes | REDACTED | At least 32 chars; rotate if exposed |
| `Jwt__Issuer` | Yes | `cambrian-api` | Must match token validation |
| `Jwt__Audience` | Yes | `cambrian-client` | Must match token validation |
| `Stripe__SecretKey` | Yes | REDACTED | Same Stripe account as price IDs |
| `Stripe__WebhookSecret` | Yes | REDACTED | Platform webhook endpoint secret |
| `Stripe__ConnectWebhookSecret` | Yes | REDACTED | Separate Connect webhook endpoint secret |
| `Stripe__Prices__Creator` | Yes | REDACTED | Server-side only |
| `Stripe__Prices__Pro` | Yes | REDACTED | Server-side only |
| `Checkout__Enabled` | Yes | Currently `false` in `render.yaml` | Keep false until launch approval |
| `Storage__Provider` | Yes | REDACTED | Must be `s3` or `r2`; app rejects local in Production |
| `Storage__Endpoint` | Yes | REDACTED | Object store endpoint |
| `Storage__Bucket` | Yes | REDACTED | Production bucket only |
| `Storage__AccessKey` | Yes | REDACTED | Least privilege object-store key |
| `Storage__SecretKey` | Yes | REDACTED | Secret manager only |
| `Storage__Region` | Yes | `us-east-1` in `render.yaml` | Required by current Supabase S3 signing config |
| `Storage__UsePathStyle` | Yes | `true` | Required by current storage integration |
| `Storage__PublicUrl` | Yes | REDACTED | Covers/media public base URL |
| `Email__Provider` | Yes | `resend` | Console provider is rejected in Production |
| `Email__FromAddress` | Yes | `noreply@cambrianmusic.com` | Sender identity |
| `Email__FromName` | Yes | `Cambrian Music` | Sender identity |
| `Email__ResendApiKey` | Yes | REDACTED | Secret manager only |
| `Email__ResendWebhookSecret` | Yes | REDACTED | Required for Resend webhook verification |
| `App__FrontendUrl` | Yes | `https://cambrianmusic.com` | Used in redirects and links |
| `App__CorsOrigins` | Yes | Explicit origins | No wildcard with credentials |
| `App__VercelProjectSlug` | Optional | Reviewed | Only set for trusted preview slug |
| `App__CloudflarePagesSlug` | Optional | Reviewed | Only set for trusted preview slug |
| `ForwardedHeaders__KnownProxies` / `KnownNetworks` | Optional | Empty in `render.yaml` | Empty means forwarded headers are not trusted |
| `Provenance__Signing__PrivateKeyPem` | Yes | REDACTED | App fails production startup when missing |
| `Provenance__Anchor__RpcUrl` / `PrivateKey` | If EVM anchoring enabled | REDACTED | Required only when provider is `evm` |
| `Sentry__Dsn` | Recommended | REDACTED | Disabled when empty |
| `Google__ClientId` | If Google OAuth enabled | REDACTED | Required for Google sign-in |
| `Admin__Email` / `Admin__Password` | Operational | REDACTED | Rotate after bootstrap; do not share |

## Control Checklist

| Area | Required state | Status |
| --- | --- | --- |
| Authentication | Signup/login/logout/refresh/password reset tested; failures safe; lockout/rate limits verified | Full suite passed locally; live verification pending |
| Cookie security | Production cookies secure; SameSite policy documented; CSRF required for cookie writes | Pending live browser/API verification |
| Authorization | Public/protected/admin/creator-owner/financial boundaries tested | Manifest fixed; full suite passed locally; live verification pending |
| Public API/MCP | Public DTOs exclude sensitive fields; page sizes capped; cache headers reviewed | Full suite passed locally; manual DTO review should continue |
| Uploads | Type/extension/size/path traversal validation; direct private upload access blocked | Full suite passed locally; malware scanning not implemented |
| Streaming | Public/private/hidden visibility enforced; range works; stream start abuse limited | Full suite passed locally; live abuse/rate-limit verification pending |
| Payments | Checkout server-side priced; webhook signature/idempotency; failed/refund/cancel safe | Full suite passed locally; live Stripe config pending |
| Stripe Connect | Owner-scoped onboarding/status; Connect IDs private; disabled accounts block monetization | Full suite passed locally; live Connect config pending |
| Entitlements | Granted/revoked only by verified server paths; credits non-negative and consumed once | Full suite passed locally; live cancellation/refund smoke pending |
| Rate limits | Login/signup/reset/upload/stream/search/MCP/checkout/follow/boost reviewed | Pending live proxy/IP verification |
| CORS | Explicit production origins; no wildcard credentials | Configured; pending deploy smoke |
| Error handling | No stack traces, SQL details, internal URLs, secrets in production responses | Pending live production-mode smoke |
| Logging | Request/payment/security events useful; no secrets/tokens/cookies/payment payloads | Pending log sampling review |
| Headers | HSTS, nosniff, frame deny, referrer policy, permissions policy | Implemented; pending smoke |
| OpenAPI/contracts | Protected endpoints documented accurately; manifest auth truth validated | Manifest fixed; contract drift still failing on pre-existing drift |
| Supply chain | NuGet/npm vulnerable package review complete | Passed locally |

## Final Approval Rule

Production can move from NO-GO only when:

1. Full backend suite passes on the deploy candidate.
2. Contract generation and drift checks pass.
3. Dependency/secret scans are complete.
4. Render/production env values are verified with secrets redacted.
5. Checkout kill switch is intentionally set for the launch decision.
6. Public data, private creator data, paid features, webhooks, and entitlements have current test evidence.
