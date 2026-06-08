# 🦕 Cambrian Backend API

> A **contract-driven** REST API for the Cambrian music platform, built with ASP.NET Core 8.

[![CI](https://github.com/CambrianHQ/Cambrian-Backend-Api/actions/workflows/ci.yml/badge.svg)](https://github.com/CambrianHQ/Cambrian-Backend-Api/actions/workflows/ci.yml)

---

## 📋 Table of Contents

- [Overview](#-overview)
- [Features](#-features)
- [API Endpoints](#-api-endpoints)
- [Creator Profile Workflow](#-creator-profile-workflow)
- [Feature Flags & Analytics Workflow](#-feature-flags--analytics-workflow)
- [Roles & Authorization](#-roles--authorization)
- [Rate Limiting](#-rate-limiting)
- [Storage & Streaming Notes](#-storage--streaming-notes)
- [Troubleshooting](#-troubleshooting)
- [Core Principles](#-core-principles)
- [Architecture](#-architecture)
- [Getting Started](#-getting-started)
- [Environment Variables](#-environment-variables)
- [Running with Docker](#-running-with-docker)
- [Deployment](#-deployment)
- [Testing](#-testing)
- [Contracts](#-contracts)
- [CI / CD](#-ci--cd)
- [Contributing](#-contributing)
- [License](#-license)

---

## 🌐 Overview

**Cambrian Backend API** is the server-side backbone of the Cambrian music platform. It exposes authenticated REST endpoints for user authentication, audio library management, streaming, purchases, creator revenue tools, subscriptions, billing, and administration — all governed by a published OpenAPI contract and enforced by an automated CI/CD test suite.

**Tech stack:**
- ⚙️ **Runtime** — .NET 8 / ASP.NET Core
- 🗄️ **Database** — PostgreSQL (via Entity Framework Core)
- 🔐 **Auth** — JWT Bearer tokens (ASP.NET Core Identity)
- 💳 **Payments** — Stripe (checkout sessions + webhooks)
- 🪣 **Object Storage** — S3-compatible (MinIO, AWS S3, Cloudflare R2)
- 📄 **API Docs** — Swagger / OpenAPI 3.x
- 🚦 **Rate Limiting** — Built-in ASP.NET Core rate limiting middleware

---

## ✨ Features

| No. | Feature | Description |
|-----|---------|-------------|
| 1 | 🔐 **Authentication & Identity** | Register, login, logout, password reset, account info, and role-based access control |
| 2 | 🎵 **Audio Library** | Add, browse, and remove tracks from a user's personal library |
| 3 | 🛒 **Checkout & Purchases** | Stripe-backed checkout sessions, purchase history, and credit-creator flows |
| 4 | 🎧 **Streaming** | Start and stop audio streams with real-time session tracking |
| 5 | ⬇️ **Downloads** | Signed URL generation with purchase-gate enforcement for purchased tracks |
| 6 | ⬆️ **Uploads** | Multipart audio file upload (up to 150 MB), gated to Creator role |
| 7 | 🔍 **Catalog & Discovery** | Browse the full track catalog, discover featured tracks, and view trending content |
| 8 | 🎙️ **Creator Tools** | Manage creator tracks, view revenue dashboards, and request payouts |
| 9 | 💰 **Wallet & Credits** | Track wallet balance and credit creators on purchases |
| 10 | 📋 **Subscriptions & Billing** | Manage subscription plans, upgrades, cancellations, and Stripe billing portals |
| 11 | 🧾 **Invoices** | Retrieve, list, and download purchase invoices (with PDF rendering) |
| 12 | 🛡️ **Admin Panel** | Manage users, review payouts, moderate tracks, and view reports and audit logs |
| 13 | 🚩 **Feature Flags** | Per-user feature checks and admin-controlled rollout percentages |
| 14 | 📈 **Analytics** | Track usage events and query admin summaries/events |
| 15 | 🎨 **Creator Profiles** | Public creator pages with editable profile media and collections |
| 16 | ❤️ **Health Checks** | API, auth service, and object-storage health probe endpoints |
| 17 | 🔑 **API Keys & Public v1** | Hashed API-key auth and the public `/api/v1/*` surface (tracks, creators, keys) |
| 18 | 🎟️ **Entitlements** | Resource grants + plan/tier entitlement matrix exposed via `/me` |
| 19 | 🚀 **Track Boosts & Community** | Paid track boosts and community "hot tracks" ranking |
| 20 | 🎚️ **Release-Ready** | Audio mastering pipeline + per-plan release credits |
| 21 | 🔏 **Provenance & Authorship** | Content hashing, ECDSA signing, batched Merkle anchoring, and authorship disclosure |
| 22 | 🤖 **MCP / AI Discovery** | Model Context Protocol resources/tools for AI-assisted track discovery |

> ℹ️ **Note on Licenses:** standalone license-certificate endpoints were retired; checkout/purchase now flows through `/billing`, and certificate/invoice PDFs are rendered server-side. See [Payments](#-api-endpoints).

---

## 🗺️ API Endpoints

All endpoints are defined in the versioned OpenAPI spec at [`contracts/openapi.v1.json`](contracts/openapi.v1.json). Below is a high-level reference by domain:

| Domain | Method | Path | Auth Required |
|--------|--------|------|:---:|
| 🔐 **Auth** | `POST` | `/auth/register` | ❌ |
| | `POST` | `/auth/login` | ❌ |
| | `POST` | `/auth/logout` | ✅ |
| | `GET` | `/auth/me` | ✅ |
| | `POST` | `/auth/forgot-password` | ❌ |
| | `POST` | `/auth/reset-password` | ❌ |
| 📚 **Library** | `GET` | `/library` | ✅ |
| | `POST` | `/library` | ✅ |
| | `DELETE` | `/library/{trackId}` | ✅ |
| | `GET` | `/library/purchased-track-ids` | ✅ |
| 🎧 **Streaming** | `GET` | `/stream/{trackId}` | ✅ |
| | `GET` | `/stream/{trackId}/audio` | ❌ |
| | `POST` | `/stream/start` | ✅ |
| | `POST` | `/stream/stop` | ✅ |
| ⬇️ **Downloads** | `GET` | `/download/{trackId}` | ✅ |
| | `GET` | `/download/{trackId}/signed` | ✅ |
| ⬆️ **Uploads** | `POST` | `/upload` | ✅ (Creator) |
| 🔍 **Catalog** | `GET` | `/catalog` | ❌ |
| | `GET` | `/discover` | ❌ |
| | `GET` | `/trending` | ❌ |
| | `GET` | `/tracks` | ❌ |
| | `GET` | `/tracks/{id}` | ❌ |
| 🎙️ **Creator** | `GET` | `/creator/tracks` | ✅ (Creator) |
| | `GET` | `/creator/revenue` | ✅ (Creator) |
| | `GET` | `/payouts/earnings` | ✅ (Creator) |
| | `GET` | `/payouts/history` | ✅ (Creator) |
| | `POST` | `/payouts/request` | ✅ (Creator) |
| | `POST` | `/payouts/connect-stripe` | ✅ (Creator) |
| | `GET` | `/payouts/connect-status` | ✅ (Creator) |
| | `GET` | `/payouts/stripe-dashboard` | ✅ (Creator) |
| | `GET` | `/payouts/account` | ✅ (Creator) |
| | `POST` | `/payouts/connect` | ✅ (Creator) |
| | `POST` | `/payouts/disconnect` | ✅ (Creator) |
| | `DELETE` | `/payouts/disconnect` | ✅ (Creator) |
| 🎨 **Creator Profile** | `GET` | `/creator-profile/{slug}` | ❌ |
| | `GET` | `/creator-profile/me` | ✅ (Creator tier) |
| | `PUT` | `/creator-profile/me` | ✅ (Creator tier) |
| | `POST` | `/creator-profile/me/banner` | ✅ (Creator tier) |
| | `POST` | `/creator-profile/me/avatar` | ✅ (Creator tier) |
| | `GET` | `/creator-profile/{slug}/collections` | ❌ |
| | `POST` | `/creator-profile/me/collections` | ✅ (Creator tier) |
| | `PUT` | `/creator-profile/me/collections/{collectionId}` | ✅ (Creator tier) |
| | `DELETE` | `/creator-profile/me/collections/{collectionId}` | ✅ (Creator tier) |
| 🚩 **Feature Flags** | `GET` | `/feature-flags/check/{name}` | ✅ |
| | `GET` | `/feature-flags` | ✅ (Admin) |
| | `PUT` | `/feature-flags/{name}` | ✅ (Admin) |
| | `DELETE` | `/feature-flags/{name}` | ✅ (Admin) |
| 📈 **Analytics** | `POST` | `/analytics/track` | ✅ |
| | `GET` | `/analytics/summary` | ✅ (Admin) |
| | `GET` | `/analytics/events` | ✅ (Admin) |
| 💰 **Wallet** | `GET` | `/wallet` | ✅ |
| | `POST` | `/purchases/credit-creator` | ✅ |
| 📋 **Subscriptions** | `GET` | `/subscriptions/plans` | ❌ |
| | `POST` | `/subscriptions/update` | ✅ |
| | `POST` | `/subscriptions/cancel` | ✅ |
| 🏦 **Billing** | `POST` | `/billing/checkout` | ✅ |
| 🧾 **Invoices** | `GET` | `/invoices` | ✅ |
| | `GET` | `/invoices/{invoiceId}` | ✅ |
| | `GET` | `/invoices/{invoiceId}/download` | ✅ |
| 🔑 **API Keys** | `GET` | `/api/v1/keys` | ✅ |
| | `POST` | `/api/v1/keys` | ✅ |
| | `DELETE` | `/api/v1/keys/{id}` | ✅ |
| 🎟️ **Entitlements** | `GET` | `/api/me/entitlements` | ✅ |
| | `GET` | `/api/entitlements/access` | ✅ |
| 🚀 **Boosts / Community** | `POST` | `/tracks/{trackId}/boost` | ✅ |
| | `GET` | `/community/hot-this-week` | ❌ |
| 🎚️ **Release-Ready** | `POST` | `/release-ready/jobs` | ✅ |
| | `GET` | `/release-ready/credits` | ✅ |
| | `POST` | `/release-ready/validate` | ✅ |
| 🔏 **Provenance** | `GET` | `/api/tracks/{id}/provenance` | ❌ |
| | `POST` | `/api/provenance/verify` | ❌ |
| | `POST` | `/api/provenance/verify-inclusion` | ❌ |
| 🛡️ **Admin** | `GET` | `/admin/dashboard` | ✅ (Admin) |
| | `GET` | `/admin/audit` | ✅ (Admin) |
| | `GET` | `/admin/integrity` | ✅ (Admin) |
| | `GET` | `/admin/users` | ✅ (Admin) |
| | `GET` | `/admin/payouts` | ✅ (Admin) |
| | `GET` | `/admin/payouts/requests` | ✅ (Admin) |
| | `GET` | `/admin/tracks` | ✅ (Admin) |
| | `GET` | `/admin/purchases` | ✅ (Admin) |
| | `GET` | `/admin/settings` | ✅ (Admin) |
| | `POST` | `/admin/settings` | ✅ (Admin) |
| | `POST` | `/admin/users/{id}/role` | ✅ (Admin) |
| | `POST` | `/admin/users/{id}/suspend` | ✅ (Admin) |
| | `POST` | `/admin/users/{id}/reactivate` | ✅ (Admin) |
| | `POST` | `/admin/users/{id}/reset-password` | ✅ (Admin) |
| | `POST` | `/admin/users/{id}/verify-creator` | ✅ (Admin) |
| | `GET` | `/admin/reports` | ✅ (Admin) |
| | `POST` | `/admin/reports/{id}/investigate` | ✅ (Admin) |
| | `POST` | `/admin/tracks/{id}/remove` | ✅ (Admin) |
| | `POST` | `/admin/tracks/{id}/restore` | ✅ (Admin) |
| | `POST` | `/admin/tracks/{id}/hide` | ✅ (Admin) |
| | `POST` | `/admin/tracks/{id}/flag` | ✅ (Admin) |
| | `POST` | `/admin/tracks/{id}/feature` | ✅ (Admin) |
| | `POST` | `/admin/tracks/{id}/pin` | ✅ (Admin) |
| | `POST` | `/admin/tracks/{id}/visibility` | ✅ (Admin) |
| ❤️ **Health** | `GET` | `/health` | ❌ |
| | `GET` | `/auth/health` | ❌ |
| | `GET` | `/health/storage` | ❌ |

> 📄 For the complete request/response schemas, see the [OpenAPI spec](contracts/openapi.v1.json) or browse the interactive **Swagger UI** at `http://localhost:8080/swagger` when the API is running.

---

## 🎨 Creator Profile Workflow

Creator profile endpoints are intended for creator-facing branding pages and are protected by the `RequireCreatorTier` filter where relevant.

**What the code enforces:**
- `PUT /creator-profile/me` requires a `slug` that is trimmed/lowercased, **3-100 chars**, and globally unique.
- `POST /creator-profile/me/banner` and `POST /creator-profile/me/avatar` require multipart field `file` and accept only `.jpg`, `.jpeg`, `.png`, `.webp` up to **10 MB**.
- Banner/avatar upload endpoints return `404` if the creator has not created a profile yet.

```bash
# Upsert creator profile
curl -X PUT "http://localhost:8080/creator-profile/me" \
  -H "Authorization: Bearer <creator-jwt>" \
  -H "Content-Type: application/json" \
  -d '{
    "slug": "dj-aurora",
    "bio": "Ambient producer and live performer.",
    "niche": "ambient",
    "socialLinks": [{"platform":"instagram","url":"https://instagram.com/djaurora"}],
    "showEarnings": false,
    "showDownloadStats": true
  }'

# Upload avatar (must use multipart/form-data)
curl -X POST "http://localhost:8080/creator-profile/me/avatar" \
  -H "Authorization: Bearer <creator-jwt>" \
  -F "file=@avatar.png"
```

---

## 🚩 Feature Flags & Analytics Workflow

### Feature flags

Feature flags are stored in the database and support deterministic percentage rollout by `(flagName, userId)` hash so the same user gets stable behavior.

- Rollout percentage is clamped to `0..100`.
- `GET /feature-flags/check/{name}` is user-facing.
- `GET|PUT|DELETE /feature-flags/*` are Admin-only.

```bash
# Admin: enable 25% rollout for creator profile UI
curl -X PUT "http://localhost:8080/feature-flags/creator_profiles" \
  -H "Authorization: Bearer <admin-jwt>" \
  -H "Content-Type: application/json" \
  -d '{"enabled": true, "rolloutPercentage": 25}'

# Authenticated user: check whether feature is enabled for this user
curl "http://localhost:8080/feature-flags/check/creator_profiles" \
  -H "Authorization: Bearer <user-jwt>"
```

### Analytics

`POST /analytics/track` accepts only these event types: `play`, `download`, `purchase`, `search`, `upload`.

- `trackId` is optional; if provided it must parse as a GUID.
- Admin queries: `GET /analytics/summary` and `GET /analytics/events`.
- Query `limit` is clamped to `1..1000`.

```bash
curl -X POST "http://localhost:8080/analytics/track" \
  -H "Authorization: Bearer <user-jwt>" \
  -H "Content-Type: application/json" \
  -d '{"eventType":"play","trackId":"11111111-1111-1111-1111-111111111111","metadata":"{\"surface\":\"catalog\"}"}'
```

---

## 👥 Roles & Authorization

The API uses **role-based access control** enforced via JWT claims:

| Role | Symbol | Access |
|------|--------|--------|
| **Anonymous** | 🌍 | Public endpoints only (catalog, health, auth) |
| **User** | 👤 | Library, checkout, downloads, streaming, wallet, subscriptions, invoices |
| **Creator** | 🎙️ | All User permissions + upload, creator dashboard, revenue, payouts |
| **Admin** | 🛡️ | Full access including the `/admin/*` management panel |

---

## 🚦 Rate Limiting

Built-in rate limiting protects the API from abuse:

| Scope | Limit |
|-------|-------|
| 🔐 Auth endpoints (`/auth/*`) | **10 requests / minute** per IP |
| 🌐 All other endpoints | **100 requests / minute** per IP |

Requests that exceed the limit receive a `429 Too Many Requests` response.

---

## 🪣 Storage & Streaming Notes

- `GET /stream/{trackId}/audio` performs a redirect to object storage (S3/R2 signed URL), allowing CDN/native range handling for browser audio playback.
- Download endpoints (`/download/*`) still enforce purchase entitlement before returning a download URL or file stream.
- Local storage (`Storage__Provider=local`) is useful for development but is ephemeral in containerized deployments.
- For Cloudflare R2:
  - `Storage__Provider=r2`
  - `Storage__Region=auto`
  - `Storage__UsePathStyle=true`
- For a Supabase / AWS S3-style gateway, set a **real AWS region** (e.g. `us-east-1`). The `auto` value is a Cloudflare-R2-only convention and produces `SignatureDoesNotMatch` against SigV4 gateways that require a concrete region.

---

## 🧯 Troubleshooting

| Symptom | Likely cause | What to check |
|---|---|---|
| Signed stream/download URL returns `403`/`SignatureDoesNotMatch` | Region/path-style mismatch | R2: `Storage__Region=auto`. Supabase/S3 gateway: a real AWS region (e.g. `us-east-1`). Keep `Storage__UsePathStyle=true`; verify endpoint/bucket/key pair |
| `POST /creator-profile/me/avatar` returns "Invalid image file." | Unsupported type, missing multipart field, or file >10 MB | Use form field name `file`; only jpg/jpeg/png/webp; keep size <= 10 MB |
| Creator profile endpoints return `403 Creator tier required.` | JWT/user tier is not `creator` | Confirm token includes tier claim or user tier is set to creator |
| Browser CORS failures from preview deploys | Missing origin allow-list | Configure `App__CorsOrigins` and `App__VercelProjectSlug` for preview environments |
| Download returns purchase-required message | User has no library entitlement for track | Verify purchase/library row exists for `(userId, trackId)` |

---

## 🏛️ Core Principles

This project is driven by three non-negotiable pillars:

### 📜 Contract Compliance
Every endpoint is defined in the versioned OpenAPI specification located in [`contracts/openapi.v1.json`](contracts/openapi.v1.json) and the endpoint manifest at [`contracts/endpoint-manifest.v1.json`](contracts/endpoint-manifest.v1.json). No endpoint ships without a corresponding contract entry. Breaking changes require a contract version bump.

### 🛡️ Policy Compliance
All routes that handle sensitive data are protected by authentication and authorization policies enforced at the framework level. Environment-specific secrets (database credentials, JWT secrets, Stripe keys, storage keys) are **never** committed to source control — see [`.env.example`](config/.env.example) for required variables. Secrets must be injected via environment variables or a secrets manager at runtime.

### ✅ Test Compliance
The test project (`tests/Cambrian.Api.Tests/`) covers auth, payments/webhooks, catalog, creator identity, entitlements, boosts, release-ready, provenance, and contract conformance. Representative suites:

| Area | File | What it verifies |
|---|---|---|
| 🔐 Auth | `AuthControllerTests.cs` | Register, login, `/auth/me`, set-username, role guards |
| 💳 Webhooks/Money | `StripeWebhookServiceTests.cs`, `ConcurrencyTests.cs` | Fulfillment, idempotency, exclusive/buyout atomicity |
| ⬆️/⬇️ Upload & Download | `UploadServiceTests.cs`, `DownloadControllerTests.cs` | Upload gating, signed URLs, purchase-gate |
| 🎟️ Entitlements | `EntitlementMatrixTests.cs` | Plan/tier entitlement resolution |
| 🚀 Boosts / 🎚️ Release-Ready | `TrackBoostTests.cs`, `ReleaseReadyCreditTests.cs` | Boost flow, mastering credits |
| 🔏 Provenance | `ProvenanceAnchorBatchTests.cs`, `ProvenanceComplianceUnitTests.cs` | Merkle anchoring, compliance scoring |
| 📜 Contract | `Contract/ApiContractTests.cs`, `AI/AiDiscoveryContractTests.cs` | Routes match the OpenAPI spec |

> ⚠️ **Honest test status (not fully green).** The non-relational suite is the day-to-day signal (~720+ passing). Two groups are **known-failing** and tracked, not regressions:
> - `AI/AiDiscoveryContractTests` — AI discovery schemas not yet present in `contracts/openapi.v1.json`.
> - `/sse` OpenAPI coverage tests — the `/sse` alias is not modelled as a contract path.
>
> Relational tests use Testcontainers and **require Docker**; without it they are skipped/excluded. Do not assume a fully green suite — run it locally and read the summary.

---

## 🏗️ Architecture

The solution follows a **Clean Architecture** pattern with clear separation of concerns:

```
Cambrian.sln
├── src/
│   ├── Cambrian.Domain          # 🧱 Core entities and domain logic
│   ├── Cambrian.Application     # ⚙️  Use cases and application services
│   ├── Cambrian.Persistence     # 🗄️  EF Core database context and migrations
│   ├── Cambrian.Infrastructure  # 🔌 External integrations (Stripe, Storage)
│   └── Cambrian.Api             # 🌐 ASP.NET Core HTTP layer (controllers, middleware)
└── tests/
    └── Cambrian.Api.Tests       # ✅ Automated test suite
```

Dependencies flow **inward only**: `Api` → `Application` → `Domain`. Infrastructure and Persistence implement interfaces defined in the inner layers.

---

## 🚀 Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [PostgreSQL](https://www.postgresql.org/) (or use the Docker Compose setup)
- [MinIO](https://min.io/) or any S3-compatible storage (optional for local dev)

### Local Setup

```bash
# 1. Clone the repository
git clone https://github.com/CambrianHQ/Cambrian-Backend-Api.git
cd Cambrian-Backend-Api

# 2. Copy and configure environment variables
cp config/.env.example .env
# Edit .env with your local values

# 3. Restore dependencies
dotnet restore Cambrian.sln

# 4. Build the solution
dotnet build Cambrian.sln --configuration Release

# 5. Start the API
npm run start:backend
```

`npm run start:backend` uses [`scripts/start-backend.ps1`](scripts/start-backend.ps1) to start the backend with development-safe defaults. It prefers an existing compiled build, enables staging/demo seeding by default for local development, and binds to `http://127.0.0.1:5055` unless that port is already in use, in which case it automatically selects the next open port.

Useful local checks:

```powershell
Invoke-WebRequest http://127.0.0.1:5055/health
```

Swagger UI is typically available at `http://127.0.0.1:5055/swagger`.

Demo account reference: [`docs/demo-accounts.md`](docs/demo-accounts.md)

---

## 🔧 Environment Variables

Copy [`.env.example`](config/.env.example) to `.env` and fill in the values. **Never commit `.env` to source control.**

| Variable | Description |
|---|---|
| `DATABASE_URL` | PostgreSQL connection string |
| `FRONTEND_URL` | Allowed CORS origin for the frontend |
| `Jwt__Issuer` | JWT token issuer |
| `Jwt__Audience` | JWT token audience |
| `Jwt__Key` | Long random secret used to sign JWT tokens (also accepts `JWT_KEY`) |
| `Stripe__SecretKey` | Stripe secret API key |
| `Stripe__WebhookSecret` | Stripe webhook signing secret |
| `Storage__Endpoint` | S3-compatible storage endpoint URL |
| `Storage__Bucket` | Storage bucket name |
| `Storage__AccessKey` | Storage access key |
| `Storage__SecretKey` | Storage secret key |
| `Storage__Region` | Storage region (`auto` for Cloudflare R2) |
| `Storage__UsePathStyle` | Use path-style S3 URLs (`true` for MinIO/R2) |
| `Storage__PublicUrl` | Optional public bucket base URL (used for cover art URLs) |
| `App__VercelProjectSlug` | Optional Vercel preview-domain allowlist slug for CORS |

---

## 🐳 Running with Docker

A `Dockerfile` and `docker-compose.yml` are provided for containerized deployment.

```bash
# Build and start the API container
docker compose up --build
```

The API will be available at `http://localhost:8080`.

> **Note:** Provide secrets via a `.env` file alongside `docker-compose.yml`. The compose file loads it automatically via `env_file`.

---

## 🚢 Deployment

Production runs on **Render's free tier** with the database on **Neon** (external Postgres), described by [`render.yaml`](render.yaml).

- **Production** deploys from `main`; **staging** deploys from `staging`. A git push is only the source-control half — a deploy is not live until Render has built and promoted it.
- `DATABASE_URL` is set manually as a Render workspace secret (Neon connection string), not auto-provisioned. All `sync: false` env vars in `render.yaml` must be filled in the dashboard.
- Migrations run automatically on startup (`app.RunMigrationsAsync()`); they are skipped in the `Testing` environment.
- `Storage__Provider` must be `s3`/`r2` in Production (the app refuses to start with `local`). See [Storage & Streaming Notes](#-storage--streaming-notes) for the region caveat.
- See [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) for the full runbook.

---

## 🧪 Testing

### Quick run (all tests)

```bash
dotnet test Cambrian.sln --configuration Release
```

### Pre-deploy checklist (recommended)

Run the integration checklist script before every deploy:

```powershell
.\scripts\pre-deploy-tests.ps1            # run all
.\scripts\pre-deploy-tests.ps1 -Filter Auth  # run only Auth tests
.\scripts\pre-deploy-tests.ps1 -Verbose       # detailed output
```

The script:
1. Builds the test project
2. Runs all integration tests against an **in-memory test server** (no database or Stripe keys needed)
3. Prints a clear **PASS / FAIL** verdict

### Run a single suite

```bash
dotnet test tests/Cambrian.Api.Tests --filter "FullyQualifiedName~AuthTests"
dotnet test tests/Cambrian.Api.Tests --filter "FullyQualifiedName~CheckoutTests"
dotnet test tests/Cambrian.Api.Tests --filter "FullyQualifiedName~LibraryTests"
dotnet test tests/Cambrian.Api.Tests --filter "FullyQualifiedName~UploadTests"
dotnet test tests/Cambrian.Api.Tests --filter "FullyQualifiedName~DownloadTests"
```

Most tests use `WebApplicationFactory<Program>` with in-memory SQLite — no external services required. Relational tests use Testcontainers and require Docker. See the [honest test status](#-test-compliance) above before treating the suite as green.

---

## 📜 Contracts

Versioned API contracts live in the [`contracts/`](contracts/) directory:

| File | Purpose |
|---|---|
| [`contracts/openapi.v1.json`](contracts/openapi.v1.json) | Full OpenAPI 3.x specification |
| [`contracts/endpoint-manifest.v1.json`](contracts/endpoint-manifest.v1.json) | Flat endpoint manifest for tooling |

**Contract-first workflow:**
1. 📝 Update the relevant contract file with the new or changed endpoint definition.
2. 💻 Implement the endpoint in the API layer.
3. ✅ Add or update the corresponding test in `ContractTests.cs` to assert compliance.
4. 🔀 Open a pull request — CI will validate that all contract tests pass.

---

## 🔄 CI / CD

Continuous integration is handled by GitHub Actions ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)).

On every push to `main` and on every pull request the pipeline:

1. ✅ Restores NuGet dependencies
2. 🔨 Builds the solution in Release configuration
3. 🧪 Runs the test suite

A local pre-push hook runs a **critical-tests** subset before allowing a push. Note the [known-failing contract suites](#-test-compliance) — CI is not currently a fully green gate, so review the build/test summary rather than assuming a pass.

---

## 🤝 Contributing

1. Fork the repository and create a feature branch from `main`.
2. Follow the **contract-first** workflow described above.
3. Ensure all existing tests continue to pass and add tests for new behaviour.
4. Open a pull request — a maintainer will review against the contract and policy requirements.

---

## 📄 License

This project is licensed under the terms found in the [LICENSE](LICENSE) file.
