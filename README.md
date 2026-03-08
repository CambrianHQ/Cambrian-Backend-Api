# 🦕 Cambrian Backend API

> A **contract-driven**, **policy-compliant**, and **test-verified** REST API built with ASP.NET Core 8.

[![CI](https://github.com/cambrian/Cambrian-Backend-Api/actions/workflows/ci.yml/badge.svg)](https://github.com/cambrian/Cambrian-Backend-Api/actions/workflows/ci.yml)

---

## 📋 Table of Contents

- [Overview](#-overview)
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

Cambrian Backend API is the server-side backbone of the Cambrian platform. It provides authenticated REST endpoints for library management, checkout workflows, and payment processing — all governed by a published OpenAPI contract and enforced by an automated test suite.

**Tech stack:**
- ⚙️ **Runtime** — .NET 8 / ASP.NET Core
- 🗄️ **Database** — PostgreSQL (via Entity Framework Core)
- 🔐 **Auth** — JWT Bearer tokens
- 💳 **Payments** — Stripe
- 🪣 **Object Storage** — S3-compatible (MinIO by default)
- 📄 **API Docs** — Swagger / OpenAPI

---

## 🏛️ Core Principles

This project is driven by three non-negotiable pillars:

### 📜 Contract Compliance
Every endpoint is defined in the versioned OpenAPI specification located in [`contracts/openapi.v1.json`](contracts/openapi.v1.json) and the endpoint manifest at [`contracts/endpoint-manifest.v1.json`](contracts/endpoint-manifest.v1.json). No endpoint ships without a corresponding contract entry. Breaking changes require a contract version bump.

### 🛡️ Policy Compliance
All routes that handle sensitive data are protected by authentication and authorization policies enforced at the framework level. Environment-specific secrets (database credentials, JWT secrets, Stripe keys, storage keys) are **never** committed to source control — see [`.env.example`](.env.example) for required variables. Secrets must be injected via environment variables or a secrets manager at runtime.

### ✅ Test Compliance
Every feature area has a corresponding test class. The CI pipeline will **block merges** when any test fails. The current test suites are:

| Test Suite | File |
|---|---|
| 🔐 Authentication | `tests/Cambrian.Api.Tests/AuthTests.cs` |
| 🛒 Checkout | `tests/Cambrian.Api.Tests/CheckoutTests.cs` |
| 📚 Library | `tests/Cambrian.Api.Tests/LibraryTests.cs` |
| 📜 Contract | `tests/Cambrian.Api.Tests/ContractTests.cs` |

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
git clone https://github.com/cambrian/Cambrian-Backend-Api.git
cd Cambrian-Backend-Api

# 2. Copy and configure environment variables
cp .env.example .env
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

Copy [`.env.example`](.env.example) to `.env` and fill in the values. **Never commit `.env` to source control.**

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
| `Storage__Region` | Storage region |
| `Storage__UsePathStyle` | Use path-style S3 URLs (`true` for MinIO) |

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

Run the full test suite locally:

```bash
dotnet test Cambrian.sln --configuration Release
```

Tests are organised by feature area and include contract compliance checks to verify that the implementation matches the published API contract. **All tests must pass before a pull request can be merged.**

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