# Cambrian Backend Threat Model

Last updated: 2026-06-30

## Scope

This model covers the Cambrian ASP.NET Core backend: authentication, authorization, public catalog/API/MCP reads, creator data, uploads/storage, streaming, payments, Stripe webhooks, Stripe Connect, entitlements, rate limiting, CORS, headers, logging, error handling, OpenAPI/manifest contracts, and production configuration.

Frontend code, Stripe dashboard configuration, cloud IAM policy, DNS, and production data migrations are referenced only where they cross backend trust boundaries.

## Assets

| Asset | Sensitivity | Notes |
| --- | --- | --- |
| User accounts, sessions, password reset codes | High | Account takeover enables paid actions and creator data access |
| Creator tracks, drafts, hidden/private tracks | High | Public catalog must expose only public content |
| Audio files and storage keys | High | Raw object keys and private audio must not be exposed |
| Creator payout/earnings data | High | Owner-only financial data |
| Stripe platform and Connect identifiers | High | Internal IDs, customer IDs, account IDs, session IDs are private |
| Entitlements, subscriptions, credits | High | Must be server-side and webhook verified |
| Admin diagnostics and feature flags | High | Admin-only operational data and mutation |
| Public catalog/profile/provenance data | Low/Medium | Public by design, still must exclude private fields |
| Logs, Sentry events, metrics | Medium/High | Must avoid secrets, cookies, tokens, payment data, raw URLs |

## Actors

| Actor | Capabilities |
| --- | --- |
| Anonymous visitor | Read public catalog/profile/provenance data and public stream audio for public tracks |
| Authenticated listener | Library, billing, subscriptions, stream sessions, downloads when entitled |
| Creator | Upload/edit/delete own tracks, manage creator profile, request payouts when Connect is valid |
| Admin | Operational diagnostics, user/track moderation, entitlement admin actions |
| API-key/MCP client | Explicit API-key integration endpoints only; cannot use generic user-auth endpoints |
| Stripe/Resend webhook sender | Provider-signed event delivery |
| Attacker | Credential stuffing, enumeration, path traversal, public API scraping, payment spoofing, webhook replay, CORS abuse |

## Public Endpoint Boundary

Public endpoints must expose only intentionally public data. Current public classes include auth bootstrap/recovery, selected catalog/discovery/search, creator public profiles/storefronts, public v1 catalog endpoints, charts/plans, public provenance verification, health liveness, provider webhooks, and public track audio when visibility allows it.

Public endpoints must not expose emails, Stripe IDs, payment data, raw storage keys, signed URLs, private/draft/hidden tracks, unpublished/admin fields, or creator private financial data.

## Threats And Mitigations

| Threat | Impact | Current mitigation | Remaining risk |
| --- | --- | --- | --- |
| Protected route advertised as public | Clients/QA may build unsafe assumptions | `EndpointManifestFactory`, metadata-aware generator, `EndpointManifestSecurityTests` | OpenAPI security semantics still need full generated-contract verification |
| Credential stuffing or password reset abuse | Account takeover | Identity lockout and auth rate-limit policy | Needs live rate-limit verification behind real proxy/IP behavior |
| Account enumeration | User discovery | Safe auth failure messaging expected | Needs full auth regression pass after current changes |
| API key used as user session | Privilege escalation | Default auth policy excludes API-key auth; API-key policy is explicit | Requires continued tests when new endpoints are added |
| Creator edits/deletes another creator's track | Data loss | capability policies plus service owner checks | Must keep owner-only tests for each edit/delete/upload path |
| Private/draft/hidden track leaks through public catalog or stream | Content exposure | shared visibility policy in catalog/stream paths | Needs all public DTO/MCP leak tests kept current |
| Raw storage key or signed URL leak | Unauthorized media access | direct uploaded audio blocked; stream/download services mediate access | Public DTO contract tests must include new fields |
| Arbitrary upload/path traversal | Storage abuse | static upload policy and upload validation tests | Malware scanning is documented but not implemented |
| Play-count or stream-session inflation | Analytics abuse | stream start/stop auth and repository logic | Needs abuse/rate-limit tests on stream starts |
| Client grants entitlement without webhook | Revenue loss | webhook fulfillment grants paid state; checkout confirm validates session ownership | Full money-path tests must pass on deploy candidate |
| Duplicate/out-of-order Stripe event | Double grant or missed revocation | Stripe event ledger/idempotency and fail-closed metadata handling | Needs full webhook lifecycle suite after any Stripe changes |
| Connect account mismatch | Funds routed to wrong creator | Connect webhook ownership checks and status handling | Requires live Connect dashboard/config verification |
| CORS wildcard with credentials | Cross-site credential abuse | explicit origin allowlist, no wildcard credentials | Preview-origin slug config must be reviewed before production |
| Error detail leak | Information disclosure | centralized exception middleware | Must confirm production responses do not include stack traces/SQL details |
| Logs leak secrets/payment data | Secret exposure | structured logging patterns and redaction expectations | Needs log sampling review in staging/production |
| Stale production config | Startup/runtime failure or insecure fallback | production startup validates critical secrets/storage/provenance | Render/env values must be verified redacted before launch |

## Payment Threat Model

Money-moving or entitlement-changing flows must follow this invariant:

1. Client can request checkout only for server-known products and server-known prices.
2. Client request alone never grants paid access, credits, authorship records, subscriptions, or payout eligibility.
3. Stripe webhook must be signed, recognized, idempotent, and tied to a known local user/product/session.
4. Failed/refunded/disputed/cancelled events must not leave paid access incorrectly active.
5. Connect monetization must require a valid creator-owned Connect account with eligible status.

## Upload And Streaming Threat Model

Uploads are untrusted. Validate type, extension, size, image/audio format where supported, and storage key normalization before persistence. Do not expose raw keys in public DTOs. Audio access should go through stream/download controllers so visibility, entitlement, and range behavior are enforced by backend code.

## MCP/Public API Threat Model

MCP and public v1 endpoints are designed for automation. They must be low-data, cacheable where appropriate, page-size capped, rate-limited by API key/IP, and limited to public DTOs. API keys are integration credentials, not user sessions.

## Security Test Expectations

Every sensitive resource should have regression tests for:

- anonymous denied where protected
- non-owner denied or 404 where owner-only
- admin-only denied to non-admin
- public DTOs exclude sensitive fields
- webhook signature required
- webhook idempotency/duplicate event safe
- checkout kill switch blocks charge creation
- private/draft/hidden tracks excluded from public and anonymous stream paths
- rate-limit policies attached to abuse-prone endpoints
- production config fails closed when critical secrets are missing
