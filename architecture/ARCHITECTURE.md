# Cambrian Architecture

> Single source of truth for system design. All teams must align to this document.
> Last updated: 2026-03-20 | Contract version: 2.3.0

---

## 1. System Overview

Cambrian is a **music licensing marketplace** where creators upload tracks and buyers purchase licenses. The system handles authentication, catalog browsing, Stripe-powered payments, digital downloads, creator storefronts, and wallet/payout flows.

```
┌──────────────────┐       HTTPS        ┌──────────────────┐       TCP        ┌──────────────────┐
│   Frontend        │ ──────────────────▶│   Backend API     │ ───────────────▶│   PostgreSQL DB   │
│   (Vercel)        │◀────────────────── │   (Render)        │◀─────────────── │   (Render)        │
│   Next.js / React │   JSON responses   │   ASP.NET 8       │   EF Core       │   v16             │
└──────────────────┘                     └────────┬─────────┘                  └──────────────────┘
                                                  │
                                         ┌────────┼─────────┐
                                         │        │         │
                                    ┌────▼───┐ ┌──▼───┐ ┌──▼────┐
                                    │ Stripe  │ │ R2/S3│ │ Email │
                                    │ Payments│ │ Store│ │ SMTP/ │
                                    │ Connect │ │      │ │ Resend│
                                    └────────┘ └──────┘ └───────┘
```

## 2. Hosting & Infrastructure

| Component | Host | Tier | Branch |
|-----------|------|------|--------|
| Backend API (Production) | Render Web Service | Starter | `main` |
| Backend API (Staging) | Render Web Service | Free | `staging` |
| Database (Production) | Render PostgreSQL 16 | Basic 256MB | — |
| Database (Staging) | Render PostgreSQL 16 | Free | — |
| Frontend | Vercel | — | `main` / `staging` |
| Object Storage | Cloudflare R2 / S3-compatible | — | — |
| Payments | Stripe (Connect for payouts) | — | — |
| Email | Resend (production) / Console (dev) | — | — |

## 3. Backend Architecture (Clean Architecture)

```
src/
├── Cambrian.Api/              # HTTP layer: Controllers, Middleware, Program.cs
│   ├── Controllers/           # 27 controllers, DTOs in/out only
│   ├── Middleware/             # Exception handling, request logging, security headers
│   └── Tools/                 # Utility classes
├── Cambrian.Application/      # Business logic layer
│   ├── DTOs/                  # All request/response shapes (single source of truth for API contract)
│   ├── Interfaces/            # Service + repository interfaces
│   └── Services/              # 21 service implementations
├── Cambrian.Domain/           # Domain models (entities, enums)
│   ├── Entities/              # 19 entity classes
│   └── Enums/                 # CreatorTier, LicenseType, UsageType, PayoutStatus, UserRole
├── Cambrian.Infrastructure/   # External integrations
│   ├── Email/                 # SMTP, Resend, Console providers
│   ├── Options/               # Typed settings (JWT, Storage, Email, Stripe)
│   ├── Storage/               # S3/R2 and local storage providers
│   └── Stripe/                # Payment gateway implementation
└── Cambrian.Persistence/      # Data access layer
    ├── CambrianDbContext.cs    # EF Core DbContext
    ├── Configurations/         # Fluent API entity configurations
    ├── Identity/               # ASP.NET Identity customization
    ├── Migrations/             # EF Core migrations (append-only)
    └── Repositories/           # 16 repository implementations
```

### Layer Rules

| Layer | May Reference | Must NOT Reference |
|-------|--------------|-------------------|
| Api (Controllers) | Application | Domain, Persistence, Infrastructure |
| Application (Services) | Domain, Interfaces | Persistence implementations, Infrastructure implementations |
| Domain (Entities) | Nothing | Everything |
| Infrastructure | Application (interfaces), Domain | Api, Persistence |
| Persistence | Application (interfaces), Domain | Api, Infrastructure |

## 4. Core Entities

### User / Creator Identity

| Field | Scope | Notes |
|-------|-------|-------|
| `Id` (string) | Internal | ASP.NET Identity primary key, never exposed publicly |
| `Email` | Private | Authentication only — NEVER used as public identity |
| `DisplayName` | Public | Display label in UI (mutable) |
| `ProfileImageUrl` | Public | User profile photo URL — stored on `AspNetUsers`, updated via `PATCH /users/me`. Read by `GET /users/:username` and `GET /settings/profile`. |
| `CoverImageUrl` | Public | User cover/banner photo URL — stored on `AspNetUsers`, updated via `PATCH /users/me`. |
| `Bio` | Public | Short user bio — stored on `AspNetUsers`, max 500 chars, updated via `PATCH /users/me`. |
| `Slug` (CreatorProfile) | Public | URL-safe unique creator identifier (e.g., `/creator/studio-nova`) |
| `CreatorTier` | Internal | Free or Pro — controls upload limits and fee rates |

> **Tier vs CreatorTier**: `ApplicationUser` has two tier-related fields:
> - `Tier` (string: "free"/"paid"/"creator"/"pro") — legacy informational field, used in JWT claims and UI display
> - `CreatorTier` (enum: Free/Pro) — **canonical** for fee calculation via `TierManifest.For()` and upload limit enforcement
>
> Always use `CreatorTier` when computing platform fees or upload limits. `Tier` may be removed in a future migration once the frontend fully adopts `CreatorTier`.

**CRITICAL RULE**: The `slug` from `CreatorProfile` is the ONLY public creator identifier. Email is NEVER exposed in public API responses. The `Artist` field in `TrackResponse` maps to the creator's `DisplayName`.

### Track

- Identified by `CambrianTrackId` (format: `CAMB-TRK-XXXXXXXX`)
- Internal GUID `Id` used for relationships
- Prices in cents: `NonExclusivePriceCents`, `ExclusivePriceCents`, `CopyrightBuyoutPriceCents`
- Status lifecycle: `available` → `exclusive_sold` → `copyright_transferred`

### Purchase

- Links Buyer (User) to Track
- Status lifecycle: `pending` → `completed` → `refunded`
- `StripeSessionId` used for webhook idempotency
- `ExpiresAt` = CreatedAt + 24h for pending purchases

### License Certificate

- Issued on successful purchase
- Types: `standard`, `non-exclusive`, `exclusive`, `copyright_buyout`
- Immutable after creation

## 5. Data Flow

### Purchase Flow
```
Frontend → POST /checkout {trackId, licenseType, usageType}
    → CheckoutService creates Stripe session
    → Stripe redirects buyer to checkout
    → Stripe fires webhook → POST /webhook/stripe
    → WebhookService confirms → CheckoutService.ConfirmAsync()
        → Creates Purchase (completed)
        → Creates LibraryItem
        → Credits creator wallet (WalletTransaction)
        → Issues LicenseCertificate
        → If exclusive: marks track exclusive_sold
        → If copyright_buyout: transfers copyright, hides track
```

### Upload Flow
```
Frontend → POST /upload (multipart: audio + coverArt + metadata)
    → UploadService validates tier limits
    → Stores files in R2/S3
    → Creates Track entity
    → Increments user.UploadCount
    → Returns {trackId, title, cambrianTrackId}
```

### Authentication Flow
```
Frontend → POST /auth/login {email, password}
    → AuthService validates credentials
    → Issues JWT (24hr, HS256) with claims: sub, email, role, tier
    → Returns {userId, email, token, tier, role}

Frontend → GET /auth/me (Bearer token)
    → Returns full UserProfileResponse with subscription info
```

## 6. Environment Configuration

All environment variables are documented in `config/.env.example`. See [BACKEND_MANIFEST.json](../manifests/BACKEND_MANIFEST.json) for the complete list.

### Startup Guards (Production)

The API will **crash on startup** if any of these are violated:

| Check | Condition | Environment |
|-------|-----------|-------------|
| JWT Key | < 32 characters | All (except Testing) |
| DATABASE_URL | Missing | All (except Testing) |
| Storage Provider | `local` | Production |
| Email Provider | `console` | Production |
| Stripe Key | `sk_test_*` | Production |

## 7. Security Model

| Mechanism | Implementation |
|-----------|---------------|
| Authentication | JWT Bearer (HS256, 24hr expiry) |
| Authorization | Role-based: Anonymous, User, Creator, Admin |
| Rate Limiting | Per-IP: 100/min global, 10/min auth (production) |
| CORS | Whitelisted origins only (Vercel preview pattern supported) |
| Stripe Webhooks | Signature verification required |
| Password Reset | 6-digit code, SHA256 hashed, 15min expiry |
| Security Headers | CSP, X-Frame-Options, HSTS (production) |

## 8. Contract Alignment

All layers MUST stay aligned. See:

- [API_CONTRACTS.md](../contracts/API_CONTRACTS.md) — endpoint shapes (single source of truth)
- [POLICY.md](../policy/POLICY.md) — engineering rules and change management
- [BACKEND_MANIFEST.json](../manifests/BACKEND_MANIFEST.json) — backend inventory
- [FRONTEND_MANIFEST.json](../manifests/FRONTEND_MANIFEST.json) — frontend inventory
- [contracts/](../contracts/) — machine-readable contracts (OpenAPI, endpoint manifest, policy)
- [SOURCE_OF_TRUTH.md](../governance/SOURCE_OF_TRUTH.md) — priority hierarchy and master index
