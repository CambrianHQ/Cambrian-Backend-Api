# 🦕 Cambrian Backend API

> A **contract-driven**, **policy-compliant**, and **test-verified** REST API built with ASP.NET Core 8.

[![CI](https://github.com/Cambrian-by-Kinetix-Interactive/Cambrian-Backend-Api/actions/workflows/ci.yml/badge.svg)](https://github.com/Cambrian-by-Kinetix-Interactive/Cambrian-Backend-Api/actions/workflows/ci.yml)

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
| 11 | 🧾 **Invoices & Licenses** | Retrieve, list, and download invoices; browse music license types |
| 12 | 🛡️ **Admin Panel** | Manage users, approve payouts, moderate tracks, view reports and audit logs |
| 13 | 🚩 **Feature Flags** | Per-user feature checks and admin-controlled rollout percentages |
| 14 | 📈 **Analytics** | Track usage events and query admin summaries/events |
| 15 | 🎨 **Creator Profiles** | Public creator pages with editable profile media and collections |
| 16 | ❤️ **Health Checks** | API, auth service, and object-storage health probe endpoints |

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
| 🛒 **Checkout** | `POST` | `/checkout` | ✅ |
| | `GET` | `/checkout/session/{sessionId}` | ✅ |
| 💳 **Payments** | `POST` | `/payments/checkout` | ✅ |
| | `GET` | `/payments/state` | ✅ |
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
| | `GET` | `/payouts` | ✅ (Creator) |
| | `POST` | `/payouts` | ✅ (Creator) |
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
| 📜 **Licenses** | `GET` | `/licenses` | ❌ |
| | `GET` | `/licenses/{licenseId}` | ❌ |
| 🛡️ **Admin** | `GET` | `/admin/users` | ✅ (Admin) |
| | `POST` | `/admin/users` | ✅ (Admin) |
| | `PATCH` | `/admin/users/{id}` | ✅ (Admin) |
| | `DELETE` | `/admin/users/{id}` | ✅ (Admin) |
| | `GET` | `/admin/payouts` | ✅ (Admin) |
| | `POST` | `/admin/payouts` | ✅ (Admin) |
| | `PATCH` | `/admin/payouts/{id}` | ✅ (Admin) |
| | `GET` | `/admin/tracks` | ✅ (Admin) |
| | `PATCH` | `/admin/tracks/{id}` | ✅ (Admin) |
| | `DELETE` | `/admin/tracks/{id}` | ✅ (Admin) |
| | `GET` | `/admin/reports` | ✅ (Admin) |
| | `GET` | `/admin/audit` | ✅ (Admin) |
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
- For Cloudflare R2, keep:
  - `Storage__Provider=r2`
  - `Storage__Region=auto`
  - `Storage__UsePathStyle=true`

---

## 🧯 Troubleshooting

| Symptom | Likely cause | What to check |
|---|---|---|
| Signed stream/download URL returns `403` from R2 | Signature mismatch config | Ensure `Storage__Region=auto` and `Storage__UsePathStyle=true`; verify endpoint/bucket/key pair |
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
Every feature area has a corresponding test class. The CI pipeline will **block merges** when any test fails. The current test suites are:

| Test Suite | File | What it verifies |
|---|---|---|
| 🔐 Auth | `tests/Cambrian.Api.Tests/AuthTests.cs` | Register, Login, /auth/me, tier, 401 guards |
| 🛒 Purchase | `tests/Cambrian.Api.Tests/CheckoutTests.cs` | POST /checkout → Stripe URL, auth gating, validation |
| 📚 Library | `tests/Cambrian.Api.Tests/LibraryTests.cs` | GET/POST/DELETE /library, purchased-track-ids |
| ⬆️ Upload | `tests/Cambrian.Api.Tests/UploadTests.cs` | Multipart upload, Creator role gate, validation |
| ⬇️ Download | `tests/Cambrian.Api.Tests/DownloadTests.cs` | Signed URL generation, purchase-gate, 403/401 |
| 📜 Contract | `tests/Cambrian.Api.Tests/ContractTests.cs` | All routes match OpenAPI spec |

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
git clone https://github.com/loganbryanx/Cambrian-Backend-Api.git
cd Cambrian-Backend-Api

# 2. Copy and configure environment variables
cp config/.env.example .env
# Edit .env with your local values

# 3. Restore dependencies
dotnet restore Cambrian.sln

# 4. Build the solution
dotnet build Cambrian.sln --configuration Release

# 5. Run the API
dotnet run --project src/Cambrian.Api
```

The API will be available at `http://localhost:8080`. Swagger UI is served at `http://localhost:8080/swagger`.

---

## 🔧 Environment Variables

Copy [`.env.example`](config/.env.example) to `.env` and fill in the values. **Never commit `.env` to source control.**

| Variable | Description |
|---|---|
| `DATABASE_URL` | PostgreSQL connection string |
| `FRONTEND_URL` | Allowed CORS origin for the frontend |
| `Jwt__Issuer` | JWT token issuer |
| `Jwt__Audience` | JWT token audience |
| `Jwt__Secret` | Long random secret for signing JWT tokens |
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

Tests use `WebApplicationFactory<Program>` with in-memory SQLite — no external services required. **All tests must pass before a pull request can be merged.**

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
3. 🧪 Runs the full test suite

**Merging is blocked if the build or any test fails.**

---

## 🤝 Contributing

1. Fork the repository and create a feature branch from `main`.
2. Follow the **contract-first** workflow described above.
3. Ensure all existing tests continue to pass and add tests for new behaviour.
4. Open a pull request — a maintainer will review against the contract and policy requirements.

---

## 📄 License

This project is licensed under the terms found in the [LICENSE](LICENSE) file.