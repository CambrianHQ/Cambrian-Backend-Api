# Engineering Policy

> Mandatory rules for all contributors — AI and human. Violations break production.
> Last updated: 2026-03-19 | Policy version: 2.1.0

---

## Quick Reference (TL;DR)

### Contracts
- No API change without updating `contracts/API_CONTRACTS.md`

### Database
- No destructive migrations
- Use additive changes only

### Identity
- Email is internal-only
- Username/slug is public identity

### Environment
- All env vars must be in `config/.env.example`

### Frontend
- No hardcoded API URLs
- No use of undefined fields

### Governance
- Any change must update:
  - contracts (`contracts/API_CONTRACTS.md`, `contracts/endpoint-manifest.v1.json`, `contracts/openapi.v1.json`)
  - manifests (`manifests/BACKEND_MANIFEST.json`, `manifests/FRONTEND_MANIFEST.json`)
  - policy (`contracts/policy.v1.json`, `governance/backend-policy.v1.json`)

---

## 1. API Shape Changes

**RULE:** No API request/response shape changes without FIRST updating `contracts/API_CONTRACTS.md`.

- Every endpoint shape is defined in `contracts/API_CONTRACTS.md`. That file is the single source of truth.
- Adding a field? Update `contracts/API_CONTRACTS.md` BEFORE writing code.
- Removing a field? **FORBIDDEN** without a deprecation cycle (minimum 2 releases).
- Renaming a field? Treat as a removal + addition — both old and new MUST be supported during migration.
- All frontend types MUST match the shapes defined in `contracts/API_CONTRACTS.md`.

**Enforcement:** PR review must verify contract file is updated alongside any DTO change.

---

## 2. Environment Variables

**RULE:** No env variable changes without updating `config/.env.example`.

- Every environment variable used by the application MUST be listed in `config/.env.example`.
- New variables → add to `config/.env.example` with a comment explaining purpose.
- Removed variables → remove from `config/.env.example` and verify no runtime references.
- `config/.env.example` MUST NEVER contain real secrets — only placeholder/structure values.
- `render.yaml` MUST be kept in sync with `config/.env.example` for deployed environments.
- Startup validation MUST enforce required variables (see Section 10).

---

## 3. Database Migrations

**RULE:** No destructive database migrations.

### Forbidden Operations
- `DROP TABLE` — NEVER
- `DROP COLUMN` — NEVER without a data migration plan reviewed by 2 engineers
- `ALTER COLUMN` that narrows type (e.g., varchar(100) → varchar(50)) — NEVER
- Renaming columns/tables — NEVER without backward-compatible alias

### Required Process
1. Migrations are **append-only** — never edit existing migration files.
2. Every migration MUST be reversible (provide `Down()` method).
3. Schema changes MUST update `manifests/BACKEND_MANIFEST.json` (tables section).
4. Data migrations MUST be tested against a staging database snapshot first.
5. Production migrations run automatically on startup — they MUST be safe for zero-downtime.

---

## 4. Identity & Privacy

**RULE:** Email is NEVER used as public identity. Username/slug is the ONLY public creator identifier.

### Email Restrictions
- Email appears ONLY in:
  - `POST /auth/login` request (authentication)
  - `POST /auth/register` request (account creation)
  - `GET /auth/me` response (private, authenticated user only)
  - `GET /settings/profile` response (private, authenticated user only)
  - `GET /admin/users` response (admin dashboard only)
  - Password reset / recovery flows
- Email MUST NOT appear in:
  - Track responses
  - Storefront/profile public responses
  - Library items
  - Public search results
  - Any response from unauthenticated endpoints

### Creator Public Identity
- `slug` (from CreatorProfile) is the URL-safe public identifier: `/creator-profile/{slug}`
- `DisplayName` maps to `Artist` in track responses
- `profileImageUrl` from CreatorProfile for avatars
- Internal `CreatorId` / `UserId` may appear for relationship linking but is NOT the public identity

---

## 5. DTO / Frontend Type Alignment

**RULE:** All frontend types must exactly match backend DTOs.

- C# DTOs (in `src/Cambrian.Application/DTOs/`) are the canonical definition.
- Frontend TypeScript types MUST be generated from or validated against these DTOs.
- No `any` types in frontend code for API responses.
- No additional fields in frontend types that don't exist in backend DTOs.
- No missing fields in frontend types that are required in backend DTOs.
- Field names use **camelCase** in JSON (C# PascalCase → JSON camelCase via default serializer).

---

## 6. No Hardcoded URLs

**RULE:** No hardcoded API URLs in any codebase.

- Frontend MUST read API base URL from environment variable (e.g., `NEXT_PUBLIC_API_URL`).
- Backend CORS origins MUST be configured via `CORS_ORIGINS` env var or `appsettings.json`.
- Stripe webhook URLs configured in Stripe dashboard, not in code.
- Storage endpoint URLs configured via `Storage:Endpoint` setting.
- Email "from" addresses configured via `Email:FromAddress` setting.

---

## 7. Backward Compatibility

**RULE:** PRIORITIZE backward compatibility in all changes.

- New fields MUST have defaults or be nullable — never break existing consumers.
- Removed fields MUST go through deprecation: mark as deprecated → 2 releases → remove.
- Enum values MUST only be appended, never reordered or removed.
- HTTP status codes MUST NOT change for existing success/error paths.
- URL paths MUST NOT change — add new paths, deprecate old ones with redirects.

---

## 8. Contract Update Requirements

Any change to the following MUST update the corresponding governance files:

| Changed | Must Update |
|---------|------------|
| Database schema (migration) | `manifests/BACKEND_MANIFEST.json` (tables), `architecture/ARCHITECTURE.md` (if entity changes) |
| API endpoint added/removed | `contracts/API_CONTRACTS.md`, `contracts/endpoint-manifest.v1.json`, `contracts/openapi.v1.json` |
| API request/response shape | `contracts/API_CONTRACTS.md`, `contracts/openapi.v1.json` |
| Environment variable | `config/.env.example`, `render.yaml`, `manifests/BACKEND_MANIFEST.json` |
| Frontend route | `manifests/FRONTEND_MANIFEST.json` |
| Frontend API usage | `manifests/FRONTEND_MANIFEST.json` |
| Service added/removed | `manifests/BACKEND_MANIFEST.json` (services) |
| Controller added/removed | `manifests/BACKEND_MANIFEST.json` (endpoints), `contracts/endpoint-manifest.v1.json` |
| Public model/enum changed | `contracts/API_CONTRACTS.md` (enums reference) |

---

## 9. Code Architecture Rules

| Rule | Enforcement |
|------|------------|
| Controllers delegate to Services only | `governance/backend-policy.v1.json` |
| No DbContext in Controllers | `governance/backend-policy.v1.json` |
| No domain entities in API responses | DTOs required, `governance/backend-policy.v1.json` |
| DB access through Repositories only | `governance/backend-policy.v1.json` |
| JWT required for protected routes | `[Authorize]` attribute |
| Admin routes require Admin role | `[Authorize(Roles="Admin")]` |
| Creator routes require Creator role | `[Authorize(Roles="Creator")]` |
| Stripe webhooks verify signature | WebhookController validates `Stripe-Signature` |
| Stripe events checked for idempotency | `StripeWebhookEvent.EventId` unique check |

---

## 10. Startup Validation

The application MUST validate on startup:

### Required (all environments except Testing)
- `Jwt:Key` or `JWT_KEY` is set and ≥ 32 characters
- Database connection string is available

### Required (Production)
- Storage provider is NOT `local` — must be `s3` or `r2`
- Email provider is NOT `console` — must be `smtp` or `resend`
- Stripe secret key starts with `sk_live_` (not `sk_test_`)
- `FRONTEND_URL` is set

### Logged on startup (safe info only)
- Environment name
- DB connection source (URI vs ADO.NET)
- JWT key presence and length
- Storage provider
- Email provider
- Stripe key prefix (first 7 chars only)

---

## 11. Testing Requirements

- All PRs must pass `scripts/pre-deploy-tests.ps1`
- Contract validation via `scripts/validate-contracts.cjs`
- No PR merged without tests for new endpoints
- Integration tests use in-memory test server (no real DB/Stripe)

---

## 12. Security Rules

- Passwords: minimum 8 chars, uppercase + lowercase + digit required
- Password reset codes: 6-digit, SHA256 hashed, 15-minute expiry
- JWT tokens: 24hr expiry, HS256 signing
- Rate limiting enforced per-IP (production: 100/min global, 10/min auth)
- CORS: explicit origin whitelist only
- Security headers: CSP, X-Frame-Options, HSTS (production)
- No sensitive data in logs (mask passwords, tokens, keys)
- Stripe webhook signature verification mandatory
- `POST /admin/purge-test-data` blocked in Production environment

---

## 13. Deployment Rules

- Production deploys from `main` branch only
- Staging deploys from `staging` branch only
- Migrations run automatically on startup — MUST be zero-downtime safe
- Health check (`GET /health`) must pass before traffic is routed
- Rollback: redeploy previous commit (migrations must be backward-compatible)
