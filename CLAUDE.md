# CLAUDE.md — Cambrian Backend API

> **For AI assistants:** This file is the authoritative reference for this codebase.
> Do not invent, assume, or extrapolate beyond what is documented here.
> When in doubt, read the source file before making changes.

---

## 1. Stack & Tech Overview

| Component | Technology | Version |
|-----------|-----------|---------|
| Runtime | .NET / ASP.NET Core | **8.0** |
| Language | C# | Latest (.NET 8) |
| SDK pin | `global.json` | `"version": "8.0.0"`, `rollForward: latestMajor` |
| Database | PostgreSQL | 16 (Render), Npgsql EF provider 8.0.11 |
| ORM | Entity Framework Core | 8.0.12 |
| Identity | ASP.NET Core Identity + JWT Bearer | 8.0.12 / System.IdentityModel.Tokens.Jwt 8.3.1 |
| OAuth | Google OAuth (ID token flow) | Google.Apis.Auth 1.73.0 |
| Payments | Stripe Connect Express | stripe.net 46.2.0 |
| Object Storage | Supabase Storage (S3-compatible) / local | AWSSDK.S3 3.7.305 |
| Email | SMTP or Resend (console in dev) | MailKit 4.15.1 |
| PDF | QuestPDF | 2026.2.3 |
| Password Hashing | BCrypt | BCrypt.Net-Next 4.0.3 |
| OpenAPI | Swashbuckle | 6.6.2 |
| Testing | xunit | 2.9.3 |
| Mocking | NSubstitute | 5.3.0 |
| Test DB | EF InMemory or SQLite | 8.0.12 |

**No Redis.** Caching uses `Microsoft.Extensions.Caching.Memory` (in-process, non-distributed).

---

## 2. Directory Structure

```
Cambrian-Backend-Api/
├── Cambrian.sln                        # Solution file
├── Dockerfile                          # Docker build (mcr.microsoft.com/dotnet/sdk:8.0)
├── render.yaml                         # Render IaC blueprint (staging + production)
├── global.json                         # SDK version pin (8.0.0)
│
├── src/
│   ├── Cambrian.Domain/                # Layer 1 — pure domain model, no external deps
│   │   └── Entities/                   # All entity classes (Track, Creator, Purchase, etc.)
│   │   └── Enums/                      # Domain enums (CreatorTier, etc.)
│   │
│   ├── Cambrian.Application/           # Layer 2 — business logic, interfaces, DTOs
│   │   ├── DTOs/                       # Request/response data transfer objects
│   │   │   ├── Auth/                   # Auth-specific DTOs
│   │   │   ├── Creator/                # Creator DTOs
│   │   │   ├── Payment/                # Payment DTOs
│   │   │   └── ...
│   │   ├── Interfaces/                 # Service and repository contracts (IXxxService, IXxxRepository)
│   │   ├── Services/                   # All business service implementations
│   │   └── Configuration/              # App-level config models (TierManifest, etc.)
│   │
│   ├── Cambrian.Persistence/           # Layer 3 — EF Core, repositories, migrations
│   │   ├── CambrianDbContext.cs        # DbContext with all entity configs
│   │   ├── Configurations/             # IEntityTypeConfiguration<T> classes
│   │   ├── Repositories/               # Repository implementations
│   │   ├── Services/                   # Persistence-level services (HealthService, etc.)
│   │   └── Migrations/                 # EF Core migration files (24 migrations)
│   │
│   ├── Cambrian.Infrastructure/        # Layer 4 — external integrations
│   │   ├── Stripe/                     # StripeWebhookService
│   │   ├── Storage/                    # LocalObjectStorage, S3ObjectStorage, R2ObjectStorage
│   │   ├── Email/                      # SmtpEmailService, ResendEmailService, ConsoleEmailService
│   │   ├── Sms/                        # ConsoleSmsService (Twilio stub present, commented out)
│   │   └── Options/                    # StorageOptions, EmailOptions, SmsOptions
│   │
│   └── Cambrian.Api/                   # Layer 5 — HTTP API (controllers, middleware, startup)
│       ├── Controllers/                # All API controllers (~30 controllers)
│       ├── Middleware/                 # Custom middleware (DevAuth, RequestLogging, SecurityHeaders)
│       ├── Program.cs                  # Application entry point
│       ├── StartupExtensions.cs        # Startup extension methods
│       ├── appsettings.json            # Base config (all values empty — secrets must be injected)
│       ├── appsettings.Development.json
│       ├── appsettings.Staging.json
│       ├── appsettings.Production.json
│       └── wwwroot/uploads/            # Local dev file storage (audio, covers)
│
├── tests/
│   └── Cambrian.Api.Tests/             # xunit integration + unit tests
│       └── CatalogServiceTests.cs      # Active test file
│
├── contracts/
│   ├── openapi.v1.json                 # Canonical OpenAPI spec — source of truth for endpoints
│   ├── endpoint-manifest.v1.json       # Endpoint manifest
│   ├── API_CONTRACTS.md                # Contract version & change log
│   └── policy.v1.json                 # Contract governance policy
│
├── governance/
│   ├── backend-policy.v1.json          # Architectural rules (enforced by convention)
│   ├── SOURCE_OF_TRUTH.md
│   ├── AI_GOVERNANCE.md
│   └── DEPLOYMENT_CHECKLIST.md
│
├── manifests/
│   ├── BACKEND_MANIFEST.json
│   └── FRONTEND_MANIFEST.json
│
├── architecture/
│   └── ARCHITECTURE.md
│
└── policy/
    └── POLICY.md
```

---

## 3. Architecture — Request Flow

```
HTTP Request
    │
    ▼
[Rate Limiter] → 429 Too Many Requests if exceeded
    │
    ▼
[CORS Middleware] → validates Origin header
    │
    ▼
[JWT Bearer Middleware] → validates token, populates ClaimsPrincipal
    │
    ▼
[ApiKeyMiddleware]                      src/Cambrian.Api/Middleware/ApiKeyMiddleware.cs
    │  • Skips if request already authenticated (JWT/cookie)
    │  • If X-API-Key header present: SHA-256 hashes it → DB lookup
    │  • Sets ClaimsPrincipal so downstream [Authorize] works normally
    │  • Rejects invalid or revoked keys with 401 (even on AllowAnonymous endpoints)
    │  • Updates LastUsedAt fire-and-forget via IServiceScopeFactory
    │
    ▼
[Controller]                           src/Cambrian.Api/Controllers/
    │  • Reads request (HTTP only)
    │  • Validates [Authorize] attributes
    │  • Calls one or more IXxxService methods
    │  • Returns OkResponse / ErrorResponse via BaseController helpers
    │
    ▼
[Service]                              src/Cambrian.Application/Services/
    │  • Owns business logic
    │  • Calls IXxxRepository for data access
    │  • May call IObjectStorage, IEmailService, IPaymentGateway
    │  • Throws exceptions; controller catches and maps to HTTP responses
    │
    ▼
[Repository]                           src/Cambrian.Persistence/Repositories/
    │  • EF Core queries against CambrianDbContext
    │  • No business logic — only data retrieval/persistence
    │
    ▼
[CambrianDbContext]                    src/Cambrian.Persistence/CambrianDbContext.cs
    │  • IdentityDbContext<ApplicationUser>
    │  • Npgsql / PostgreSQL
    │
    ▼
[PostgreSQL Database]
```

### Governance rules (backend-policy.v1.json)

- Controllers must only contain HTTP logic (no business logic).
- Business logic lives exclusively in Services.
- Database access only through Repositories (controllers must not inject DbContext directly).
- Controllers must return DTOs, never domain entities.
- All protected endpoints must have `[Authorize]`.
- Admin endpoints require `[Authorize(Roles="Admin")]`.
- Creator endpoints require `[Authorize(Roles="Creator")]`.
- All endpoints must exist in `contracts/openapi.v1.json`.
- Stripe webhook handlers must check idempotency keys.

---

## 4. Database Schema

**DbContext:** `src/Cambrian.Persistence/CambrianDbContext.cs`
Inherits `IdentityDbContext<ApplicationUser>`.

### Tables

#### AspNetUsers (ApplicationUser : IdentityUser)
| Column | Type | Notes |
|--------|------|-------|
| Id | string (PK) | ASP.NET Identity GUID string |
| UserName | string | Identity username |
| Email | string | Unique, required |
| DisplayName | string? | User's display name |
| Role | string | `User`, `Admin`, `Creator` |
| Status | string | `active`, `suspended` |
| Tier | string | `free`, `paid`, `creator`, `pro` |
| VerifiedCreator | bool | |
| CreatorTier | enum (int) | `Free=0`, `Pro=1` |
| UploadCount | int | Denormalized; fast limit checks |
| SubscriptionStatus | string | `Active`, `Inactive`, `Cancelled` |
| SubscriptionEndDate | DateTime? | |
| StripeAccountId | string? | Stripe Connect Express `acct_xxx` |
| WalletBalanceCents | long | |
| PasswordResetCode | string? | Hashed 8-char code |
| PasswordResetCodeExpiry | DateTime? | |
| ProfileImageUrl | string? | max 500 |
| CoverImageUrl | string? | max 500 |
| Bio | string? | max 500 |
| GoogleId | string? | Google OAuth subject |
| AuthProvider | string? | `Local`, `Google` |
| CreatedAt | DateTime | |

#### Tracks
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| CambrianTrackId | varchar(25) | Unique, e.g. `CAMB-TRK-A1B2C3D4` |
| Title | varchar(200) | Required |
| Description | string? | |
| Genre | string? | |
| Mood | varchar(50) | |
| Tempo | varchar(30) | |
| Instrumental | bool | |
| Price | decimal | Legacy field |
| Duration | string? | |
| LicenseType | string? | |
| AudioUrl | string? | S3/R2 key or local path |
| CoverArtUrl | string? | |
| NonExclusivePriceCents | int | |
| ExclusivePriceCents | int | |
| CopyrightBuyoutPriceCents | int | |
| ExclusiveSold | bool | |
| Status | varchar(30) | `available`, `exclusive_sold`, `copyright_transferred` |
| Visibility | varchar(20) | `public`, `limited`, `hidden` |
| CopyrightOwnerId | string? | Changes on buyout |
| CopyrightTransferredAt | DateTime? | |
| OriginalCreatorId | string? | Preserved post-transfer |
| CreatorId | string (FK) | → AspNetUsers.Id (legacy) |
| CreatorUuid | Guid? (FK) | → Creators.Id (canonical) |
| Tags | string | Comma-separated, stored as list |
| UseCase | varchar(100) | `vlog`, `podcast`, `gaming`, etc. |
| TrendingScore | decimal | Default 0 |
| CreatedAt | DateTime | |

**Indexes:** `CambrianTrackId` (unique), `CreatorUuid`

#### Creators
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| UserId | varchar(450) | Unique FK → AspNetUsers.Id, ON DELETE RESTRICT |
| Username | varchar(40) | Unique, normalized lowercase |
| DisplayName | varchar(100) | |
| Bio | varchar(2000) | |
| ProfileImageUrl | varchar(500) | |
| CoverImageUrl | varchar(500) | |
| SocialLinks | varchar(2000) | JSON array of `{platform, url}` |
| CreatedAt | DateTime | |
| UpdatedAt | DateTime | |

#### CreatorFollows
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| FollowerId | varchar(450) | ApplicationUser.Id |
| CreatorId | Guid (FK) | → Creators.Id, ON DELETE CASCADE |
| CreatedAt | DateTime | |

**Unique index:** `(FollowerId, CreatorId)` — prevents duplicate follows.

#### Purchases
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| BuyerId | string (FK) | → AspNetUsers.Id, ON DELETE RESTRICT |
| TrackId | Guid (FK) | → Tracks.Id, ON DELETE RESTRICT |
| AmountCents | int | |
| PaymentMethod | string | `stripe` |
| LicenseType | string | `nonexclusive`, `exclusive`, `copyright_buyout` |
| UsageType | varchar(30) | `personal`, `youtube`, `ads`, `podcast`, etc. |
| Status | string | `pending`, `completed`, `refunded` |
| StripeSessionId | varchar(255) | Unique (filtered, nullable) |
| LicenseId | Guid? (FK) | → LicenseCertificates.Id, ON DELETE SET NULL |
| CompletedAt | DateTime? | |
| ExpiresAt | DateTime? | |
| CreatedAt | DateTime | |
| UpdatedAt | DateTime | |

**Unique index:** `StripeSessionId` WHERE NOT NULL

#### Library (LibraryItems)
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| UserId | string (FK) | → AspNetUsers.Id, ON DELETE CASCADE |
| TrackId | Guid (FK) | → Tracks.Id, ON DELETE RESTRICT |
| PurchaseId | Guid? (FK) | → Purchases.Id, ON DELETE SET NULL |
| Title | string | Denormalized from Track |
| Artist | string | |
| AudioUrl | string? | |
| SavedAt | DateTime | |

**Unique index:** `(UserId, TrackId)`

#### LicenseCertificates
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| TrackId | varchar(25) | CambrianTrackId |
| BuyerId | string (FK) | → AspNetUsers.Id, ON DELETE RESTRICT |
| CreatorId | string (FK) | → AspNetUsers.Id, ON DELETE RESTRICT |
| PurchaseId | Guid (FK) | → Purchases.Id, ON DELETE RESTRICT |
| LicenseType | varchar(30) | |
| UsageType | varchar(30) | default `personal` |
| CopyrightOwner | varchar(200) | |
| AllowedUses | string | Comma-separated list |
| Restrictions | string | Comma-separated list |
| IssuedAt | DateTime | |

#### Payouts
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| CreatorId | string (FK) | → AspNetUsers.Id, ON DELETE RESTRICT |
| AmountCents | long | |
| Status | string | `pending`, `completed`, `failed` (set by `PayoutService.RequestAsync`) |
| FailureReason | string? | Populated when `Status = "failed"` |
| RequestedAt | DateTime | |
| CompletedAt | DateTime? | |

#### WalletTransactions
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| UserId | string (FK) | → AspNetUsers.Id, ON DELETE CASCADE |
| AmountCents | long | |
| Type | string | `credit`, `debit` |
| Description | string | |
| RelatedPurchaseId | Guid? | |
| CreatedAt | DateTime | |

#### Invoices
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| UserId | string (FK) | → AspNetUsers.Id, ON DELETE CASCADE |
| PurchaseId | Guid (FK) | → Purchases.Id, ON DELETE RESTRICT |
| AmountCents | int | |
| Currency | string | `usd` |
| Status | string | `paid`, `pending` |
| IssuedAt | DateTime | |
| PaidAt | DateTime? | |

#### StripeWebhookEvents
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| EventId | varchar(255) | **Unique** — Stripe event ID, idempotency key |
| EventType | varchar(100) | e.g. `checkout.session.completed` |
| Status | varchar(20) | `received`, `processing`, `completed`, `failed` |
| Payload | text | Raw JSON |
| ErrorMessage | varchar(2000) | |
| Processed | bool | |
| ReceivedAt | DateTime | |
| ProcessedAt | DateTime | |

#### Subscriptions
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| UserId | string (FK) | → AspNetUsers.Id, ON DELETE CASCADE |
| Plan | string | `free`, `paid`, `creator` |
| Status | string | `active`, `cancelled`, `expired` |
| StartedAt | DateTime | |
| ExpiresAt | DateTime? | |

#### StreamSessions
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| TrackId | Guid (FK) | → Tracks.Id, ON DELETE CASCADE |
| UserId | string? | |
| StartedAt | DateTime | |
| EndedAt | DateTime? | |

#### AnalyticsEvents
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| EventType | varchar(64) | Required |
| TrackId | Guid? | |
| UserId | string? | |
| Metadata | varchar(500) | |
| IsSimulated | bool | Default false |
| CreatedAt | DateTime | |

**Indexes:** `EventType`, `CreatedAt`, `(TrackId, EventType, CreatedAt)` as `ix_analytics_events_track_type_created`

#### FeatureFlags
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| Name | varchar(100) | Unique |
| Description | string? | |
| Enabled | bool | |
| RolloutPercentage | int | 0–100 |

#### CreatorProfiles
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| UserId | varchar(450) | Unique |
| Slug | varchar(100) | Unique — routable public URL handle |
| Bio | varchar(2000) | |
| Niche | varchar(100) | |
| SocialLinks | varchar(2000) | JSON |
| BannerImageUrl | varchar(500) | |
| ProfileImageUrl | varchar(500) | |
| ShowEarnings | bool | |
| ShowDownloadStats | bool | |

#### TrackCollections
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| CreatorId | varchar(450) | |
| Title | varchar(200) | Required |
| Description | varchar(2000) | |
| CoverImageUrl | varchar(500) | |
| TrackIds | varchar(5000) | Comma-separated Guid list |

#### ActivityItems
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| Type | string | `sale`, `upload`, `follow`, etc. |
| TrackId | Guid? | |
| UserId | string? | |
| SourceId | Guid? | Related entity ID |
| IsSimulated | bool | |
| CreatedAtUtc | DateTime | |

Configuration applied via `ActivityItemConfiguration` (`IEntityTypeConfiguration<ActivityItem>`).

#### ApiKeys
| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| UserId | varchar(450) (FK) | → AspNetUsers.Id, ON DELETE CASCADE |
| KeyHash | text | SHA-256 hex of raw key — **unique index** — raw key never stored |
| KeyPrefix | varchar(8) | Display prefix only, e.g. `cbr_0544` |
| Name | varchar(100) | User-assigned label |
| CreatedAt | DateTime | |
| LastUsedAt | DateTime? | Updated fire-and-forget on each authenticated request |
| IsActive | bool | `false` = soft-deleted / revoked |

**Indexes:** `IX_ApiKeys_KeyHash` (unique), `IX_ApiKeys_UserId`

#### ASP.NET Identity Tables (managed by IdentityDbContext)
`AspNetRoles`, `AspNetRoleClaims`, `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserRoles`, `AspNetUserTokens`

---

## 5. Migrations (in order)

All migrations: `src/Cambrian.Persistence/Migrations/`

| # | Migration ID | Description |
|---|-------------|-------------|
| 1 | `20260306191920_InitialCreate` | AspNetUsers, Tracks, Purchases, Library, Payouts |
| 2 | `20260306195838_AddTrackDescription` | Track.Description column |
| 3 | `20260307174921_AddInvoiceTable` | Invoice table |
| 4 | `20260309012356_AddStripeWebhookEventLedger` | StripeWebhookEvent table + EventId unique index |
| 5 | `20260310045309_AddPasswordResetCodeFields` | PasswordResetCode, PasswordResetCodeExpiry on ApplicationUser |
| 6 | `20260310182907_RenamePurchaseAmountToAmountCents` | Purchase.Amount → AmountCents |
| 7 | `20260310222238_AddStripeAccountId` | ApplicationUser.StripeAccountId |
| 8 | `20260311031713_AddCoverArtUrl` | Track.CoverArtUrl |
| 9 | `20260312135748_AddTrackIdLicenseCertificateUsageTypeSearchFilters` | LicenseCertificate table + search indexes |
| 10 | `20260312193027_StateIntegrityFixes` | Idempotency + state consistency fixes |
| 11 | `20260313180000_AddCopyrightBuyoutSupport` | Track.CopyrightOwnerId, CopyrightTransferredAt, OriginalCreatorId |
| 12 | `20260313215553_AddCopyrightBuyoutPriceCents` | Track.CopyrightBuyoutPriceCents |
| 13 | `20260314181559_AddObservabilityFields` | Observability/logging columns |
| 14 | `20260316210738_AddAnalyticsAndFeatureFlags` | AnalyticsEvent + FeatureFlag tables |
| 15 | `20260317030433_AddCreatorProfilesAndCollections` | CreatorProfile + TrackCollection tables |
| 16 | `20260319184717_AddMissingUserColumns` | Additional ApplicationUser columns |
| 17 | `20260320024634_AddUserProfileFields` | ApplicationUser.Bio, ProfileImageUrl, CoverImageUrl |
| 18 | `20260323025948_AddCreatorsIdentityTable` | Creator table (UUID-based identity) |
| 19 | `20260323030051_SeedCreatorIdentityFeatureFlags` | Seed feature flags for creator identity rollout |
| 20 | `20260325001516_AddGoogleIdentityFields` | ApplicationUser.GoogleId, AuthProvider |
| 21 | `20260325224712_AddWebhookEventStatusColumns` | StripeWebhookEvent.Status column |
| 22 | `20260326120000_AddCreatorFollows` | CreatorFollow table |
| 23 | `20260327000000_AddIdempotencyUniqueIndexes` | Unique indexes on StripeSessionId, EventId |
| 24 | `20260327014723_AddActivityAndGrowthFeatures` | ActivityItem table + growth feature columns |
| 25 | `20260406220049_AddApiKeysTable` | ApiKeys table, unique KeyHash index, FK → AspNetUsers cascade |

**How to create a migration:**
```bash
dotnet ef migrations add <MigrationName> \
  --project src/Cambrian.Persistence \
  --startup-project src/Cambrian.Api
```

**Naming convention:** `YYYYMMDDHHMMSS_DescriptivePascalCaseName`

**How migrations run:** Automatically at startup via `app.RunMigrationsAsync()` in `Program.cs`. Skipped in the `Testing` environment.

---

## 6. Stripe Integration

> ⚠️ **PROTECTED — see Section 11 before modifying anything in this section.**

### Configuration

| Config Key | Env Var | Purpose |
|-----------|---------|---------|
| `Stripe:SecretKey` | `Stripe__SecretKey` | Stripe secret key (`sk_test_...` or `sk_live_...`) |
| `Stripe:WebhookSecret` | `Stripe__WebhookSecret` | Webhook signing secret (`whsec_...`) |

- Production: must use `sk_live_` key. Startup throws if `sk_test_` is used in production.
- Without `WebhookSecret`, all webhook requests are rejected with `InvalidOperationException`.

### Stripe Connect

**Model: Express accounts**

- Creators connect their own Stripe account via Stripe Connect Express.
- The connected account ID (`acct_xxx`) is stored in `ApplicationUser.StripeAccountId`.
- `CreatorConnectService` handles the OAuth flow.
- Platform fee rate is determined by `TierManifest.For(creatorUser.CreatorTier).FeeRate`.

### Webhook Endpoint

```
POST /webhook/stripe
```

Handled by `StripeWebhookService` (`src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs`).

### Webhook Processing Flow

1. **Signature verification** — `EventUtility.ConstructEvent(payload, signature, _webhookSecret)`. Rejects if secret missing or signature invalid.
2. **Idempotency check** — queries `StripeWebhookEvents` by `EventId`. Skips if already processed.
3. **Persist event** — inserts `StripeWebhookEvent` with `Status = "received"` before any processing.
4. **Set status to `"processing"`** — saved immediately.
5. **Process in transaction** — business logic runs inside a DB transaction.
6. **On success** — `Status = "completed"`, `Processed = true`, transaction committed.
7. **On failure** — transaction rolled back, `Status = "failed"`, `ErrorMessage` stored. Exception re-thrown (returns 500 to Stripe → Stripe retries).

### Handled Events

| Event | Handler | Action |
|-------|---------|--------|
| `checkout.session.completed` | `HandleCheckoutCompleted` | Routes by `clientReferenceId` format |
| `customer.subscription.deleted` | `HandleSubscriptionDeleted` | Marks subscription expired |
| `invoice.payment_failed` | `HandleInvoicePaymentFailed` | Logs, notifies creator |
| `charge.refunded` | `HandleChargeRefunded` | Updates purchase to `refunded` |
| `charge.dispute.created` | `HandleChargeDisputeCreated` | Logs dispute |

### clientReferenceId Formats

| Format | Path | Used by |
|--------|------|---------|
| `userId:trackId:licenseType` | Track purchase | `CheckoutService` |
| `userId:trackId:licenseType:usageType` | Track purchase with usage | `CheckoutService` |
| `userId:subscription:tier` | Subscription billing | `BillingController` |
| Raw `Guid` (legacy) | Legacy purchase ID | `PaymentService` (legacy path) |

### Race Condition Protection

**Exclusive license:** Raw SQL atomic update:
```sql
UPDATE "Tracks" SET "ExclusiveSold" = true
WHERE "Id" = {trackId} AND "ExclusiveSold" = false
```
Returns 0 rows → another request won the race → skip.

**Copyright buyout:** Raw SQL atomic update sets `ExclusiveSold`, `Status`, `Visibility`, `OriginalCreatorId`, `CopyrightOwnerId`, `CopyrightTransferredAt` atomically with conditions.

### Dead-Letter Handling

Unrecognized `clientReferenceId` or missing track → logs `[DEAD-LETTER]` warning and returns without throwing. Event persists as `"completed"` (to avoid Stripe retry storms), but fulfillment did not occur. Requires manual investigation.

---

## 7. Object Storage (Supabase / Local)

**Interface:** `IObjectStorage` (`src/Cambrian.Application/Interfaces/IObjectStorage.cs`)

```csharp
Task<string> UploadAsync(Stream file, string key, string contentType);
string GenerateSignedUrl(string key);       // time-limited GET URL
string GetPublicUrl(string key);            // permanent public URL
Task<StorageFile?> OpenReadAsync(string key);
Task DeleteAsync(string key);
```

### Providers

| Provider | Class | When Used |
|----------|-------|-----------|
| `local` | `LocalObjectStorage` | Development. Files under `wwwroot/uploads/`. |
| `s3` | `S3ObjectStorage` | Production/Staging — Supabase Storage (S3-compatible API). |

**Production requirement:** `Storage:Provider` must be `s3`. Startup throws if `local` is used in Production.

### Configuration Keys

| Key | Env Var | Notes |
|-----|---------|-------|
| `Storage:Provider` | `Storage__Provider` | `local` or `s3` |
| `Storage:Endpoint` | `Storage__Endpoint` | Supabase S3 endpoint URL |
| `Storage:Bucket` | `Storage__Bucket` | Bucket name |
| `Storage:AccessKey` | `Storage__AccessKey` | Supabase storage access key ID |
| `Storage:SecretKey` | `Storage__SecretKey` | Supabase storage secret key |
| `Storage:Region` | `Storage__Region` | Supabase storage region (e.g. `us-east-1`) |
| `Storage:UsePathStyle` | `Storage__UsePathStyle` | `true` for Supabase |
| `Storage:PublicUrl` | `Storage__PublicUrl` | Base URL for public file access |
| `Storage:LocalPath` | `Storage__LocalPath` | Dev only: `wwwroot/uploads` |

### Bucket Names

| Environment | Bucket |
|------------|--------|
| Development | local disk |
| Staging | `cambrian-audio-staging` |
| Production | `cambrian-audio-prod` |

### Audio Streaming Flow

1. Client requests `GET /stream/{trackId}/audio`
2. Server looks up `Track.AudioUrl` (S3 key)
3. Server calls `IObjectStorage.GenerateSignedUrl(key)` → presigned URL
4. Server returns HTTP `302 Redirect` to presigned URL
5. Client (or CDN) fetches audio directly from Supabase Storage with Range request support
6. Server never buffers audio data

### Upload Flow (Presigned URL)

1. Client calls `POST /api/uploads/creator-image-url` with `{type, fileName, contentType}`
2. Server generates presigned PUT URL: `{uploadUrl, publicUrl}`
3. Client PUTs file directly to `uploadUrl`
4. Client uses `publicUrl` immediately

### Local Dev Static File Blocking

Direct access to `/uploads/` is blocked with `403 Forbidden` in `Program.cs`. Audio files must be served via `/stream/{trackId}/audio`. Cover images (`/uploads/covers/`) are allowed.

---

## 8. Caching

**There is no Redis in this project.** The only caching dependency is `Microsoft.Extensions.Caching.Memory` (in-process, non-distributed). No distributed cache is configured. Any caching that exists is ephemeral and per-instance.

---

## 9. Authentication & Authorization

### JWT Bearer

| Setting | Value |
|---------|-------|
| Issuer | `cambrian-api` |
| Audience | `cambrian-client` |
| Minimum key length | 32 characters |
| Clock skew | 2 minutes |
| Validation | Issuer ✓, Audience ✓, Lifetime ✓, Signing key ✓ |

JWT key resolution order:
1. `Jwt:Key` in appsettings / user-secrets
2. `Jwt__Key` environment variable
3. `JWT_KEY` environment variable
4. Throws `InvalidOperationException` if missing (except `Testing` environment)

### Roles

| Role | Assigned when | Capabilities |
|------|--------------|--------------|
| `User` | Registration | Buy tracks, stream, library |
| `Creator` | Username set via `POST /auth/set-username` | Upload tracks, payouts, creator profile |
| `Admin` | Seeded via `Admin:Email` config | Full admin access |

### Password Policy

Minimum 8 characters, requires: digit, lowercase, uppercase, non-alphanumeric character.
Example valid: `Password123!`

### Password Reset Flow

1. `POST /auth/forgot-password` — email or phone number
2. Server generates 8-char random alphanumeric code (excludes 0/O, 1/I), hashes it, stores in `ApplicationUser.PasswordResetCode` with 15-minute expiry
3. Code sent via email or SMS
4. `POST /auth/verify-code` — validates code
5. `POST /auth/reset-password` — code + new password

### Google OAuth

- Flow: Client sends Google ID token to `POST /auth/google`
- Server validates token via `Google.Apis.Auth.GoogleJsonWebSignature.ValidateAsync`
- Google Client ID loaded from `Google:ClientId` config
- `GET /auth/google/status` returns whether Google OAuth is configured (no credentials needed)
- Account linking: `POST /auth/link-google` links a Google account to an existing local account

### API Key Auth (ApiKeyMiddleware)

| Property | Value |
|----------|-------|
| Header | `X-API-Key` |
| Format | `cbr_` + 32 random bytes as lowercase hex (68 chars total) |
| Storage | SHA-256 hash only — raw key returned **once** at creation, never persisted |
| Revocation | Soft-delete (`IsActive = false`) via `DELETE /api/v1/keys/{id}` |
| Scope | Any authenticated user can create keys |
| Key management | Requires JWT — cannot use an API key to create/list/revoke API keys |
| `LastUsedAt` | Updated fire-and-forget via `IServiceScopeFactory` (non-critical) |

Claims set on authenticated API key request: `ClaimTypes.NameIdentifier` = userId, `auth_method` = `api_key`.

### Authorization Attributes

```csharp
[Authorize]                         // any authenticated user (JWT, cookie, or API key)
[Authorize(Roles = "Admin")]        // admin only
[Authorize(Roles = "Creator")]      // creator role required
[AllowAnonymous]                    // public endpoint (API key still rejected if present but invalid)
```

---

## 10. Governance Rules

### 10.1 Files That Must Not Be Modified Without Explicit Instruction

| File | Reason |
|------|--------|
| `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs` | Core payout/purchase integrity — see Section 11 |
| `src/Cambrian.Persistence/CambrianDbContext.cs` | Schema definitions for all entities |
| `src/Cambrian.Persistence/Migrations/` | Migrations are immutable once applied |
| `contracts/openapi.v1.json` | API contract source of truth |
| `governance/backend-policy.v1.json` | Architectural rules |
| `render.yaml` | Production deployment configuration |
| `Dockerfile` | Container build process |

### 10.2 Creator Payout Flow — PROTECTED

> ⚠️ **This flow handles real money. Do not modify without explicit human approval and review.**

**The complete flow:**

```
1. Buyer completes Stripe checkout
        ↓
2. Stripe sends webhook: checkout.session.completed
        ↓
3. StripeWebhookService.HandleStripeAsync()
   - Verifies Stripe signature
   - Checks EventId for duplicates (idempotency)
   - Persists event as "received"
        ↓
4. HandleTrackPurchase()
   - Resolves userId, trackId, licenseType, usageType from clientReferenceId
   - For exclusive: atomic SQL UPDATE with ExclusiveSold = false condition
   - For copyright_buyout: atomic SQL UPDATE with multi-field condition
   - Creates Purchase record (status = "completed")
   - Adds track to buyer's Library
        ↓
5. Creator wallet credit (inside same DB transaction):
   - Looks up creator's CreatorTier
   - platformFeeRate = TierManifest.For(creatorTier).FeeRate
   - creatorCents = floor(grossCents × (1 − feeRate))
   - Inserts WalletTransaction (type = "credit")
        ↓
6. LicenseService.IssueCertificateAsync() — issues LicenseCertificate
        ↓
7. Invoice created (non-critical — failure logged but does not block)
        ↓
8. ActivityItem created (non-critical — failure logged but does not block)
        ↓
9. DB transaction committed
        ↓
10. Creator requests payout via POST /payouts
    - PayoutService.RequestAsync opens a Serializable transaction:
      (a) verifies wallet balance ≥ requested amount
      (b) writes the WalletTransaction debit
      (c) inserts the Payout record with Status = "pending"
      — all three commit atomically, or none do.
        ↓
11. Outside the transaction, PayoutService calls Stripe Connect to transfer funds:
    - On success → Payout.Status = "completed", CompletedAt set.
    - On failure → a compensating WalletTransaction credit is written, Payout.Status = "failed",
      FailureReason stored, caller gets a retryable error.
    There is no admin approve/reject step — the flow is fully creator-initiated and
    self-compensating. The `/admin/payouts/{id}/approve` and `/reject` endpoints were
    removed; attempting to re-add them risks double-crediting the wallet.
```

**Protected invariants:**
- Creator wallet credit is always computed as `floor(gross × (1 − feeRate))` — never rounded up.
- The platform fee rate comes from `TierManifest` — do not hardcode rates.
- The exclusive-sale atomic SQL must remain a raw SQL conditional UPDATE (not EF tracked entity update) to prevent race conditions.
- The copyright buyout atomic SQL must update all six fields in a single conditional statement.
- `StripeWebhookEvent.EventId` unique index ensures idempotency — never remove this index.
- `Purchase.StripeSessionId` unique filtered index ensures one purchase per Stripe session — never remove this index.

### 10.3 Stripe Connect Configuration — PROTECTED

> ⚠️ **Do not modify Stripe Connect account type, fee structure, or payout logic without explicit human approval.**

- Account type: **Express** (not Standard, not Custom)
- Fee computation: `TierManifest.For(creatorTier).FeeRate` — do not bypass
- StripeAccountId stored on `ApplicationUser.StripeAccountId` — do not rename or move
- Webhook signature verification is non-negotiable — never add a bypass or fallback
- Production must use `sk_live_` key — startup enforces this

### 10.4 Naming Conventions

**Controllers:**
- File: `XxxController.cs`
- Class: `XxxController : BaseController`
- Route: attribute routing, e.g. `[Route("api/xxx")]`
- Async actions: `GetXxxAsync`, `PostXxxAsync` (or `Get`, `Post` by convention)
- Return types: `IActionResult` or `ActionResult<T>`

**Services:**
- Interface: `IXxxService` in `Cambrian.Application/Interfaces/`
- Implementation: `XxxService` in `Cambrian.Application/Services/`
- All methods async: `Task<T>` return types

**Repositories:**
- Interface: `IXxxRepository` in `Cambrian.Application/Interfaces/`
- Implementation: `XxxRepository` in `Cambrian.Persistence/Repositories/`

**DTOs:**
- Request: `XxxRequest` (e.g. `RegisterRequest`, `PaymentCheckoutRequest`)
- Response: `XxxResponse` or `XxxDto` (e.g. `CreatorProfileDto`)
- Location: `Cambrian.Application/DTOs/<Feature>/`

**Entities:**
- Location: `Cambrian.Domain/Entities/`
- No business logic in entities — plain POCO with navigation properties
- Money stored as `int` or `long` cents (never `decimal` or `float` for prices)

**Migrations:**
- Format: `YYYYMMDDHHMMSS_DescriptivePascalCaseName`
- Always additive where possible (add columns/tables, avoid drops)
- New nullable columns or columns with defaults to avoid breaking existing data

### 10.5 How Migrations Are Created and Run

**Creating:**
```bash
dotnet ef migrations add <MigrationName> \
  --project src/Cambrian.Persistence \
  --startup-project src/Cambrian.Api
```

**Running (manual):**
```bash
dotnet ef database update \
  --project src/Cambrian.Persistence \
  --startup-project src/Cambrian.Api
```

**Running (automatic):** Migrations run automatically on startup via `app.RunMigrationsAsync()`. Skipped in `Testing` environment. Errors are caught and logged (startup continues).

**Never edit** a migration file after it has been applied to any environment. Create a new migration to fix mistakes.

### 10.6 Deployment to Render

**Platform:** Render (render.com) via Infrastructure-as-Code in `render.yaml`.

**Resources:**
| Resource | Staging | Production |
|---------|---------|-----------|
| Service name | `cambrian-api-staging` | `cambrian-api` |
| Branch | `staging` | `main` |
| Plan | free | starter |
| Runtime | docker | docker |
| Database | `cambrian-db-staging` (free, PostgreSQL 16) | `cambrian-db-prod` (basic-256mb, PostgreSQL 16) |
| Region | oregon | oregon |
| Port | 10000 | 10000 |
| Health check | `GET /health` | `GET /health` |

**Build trigger:** Changes to `src/**`, `Dockerfile`, or `Cambrian.sln`.

**Deployment flow:**
1. Push to `main` (production) or `staging` (staging) branch
2. Render detects change via `buildFilter`
3. Render builds Docker image using `Dockerfile`
4. Docker image: `mcr.microsoft.com/dotnet/sdk:8.0` (build) → `mcr.microsoft.com/dotnet/aspnet:8.0` (runtime)
5. Container starts, runs migrations, seeds data, starts serving
6. Render health-checks `/health` before routing traffic

**Dockerfile:**
- Build stage: `dotnet publish` in Release mode to `/app/publish`
- Runtime stage: non-root user `appuser:appgroup` (uid/gid 1001)
- Entrypoint: `exec env ASPNETCORE_URLS=http://+:$PORT dotnet Cambrian.Api.dll`
- `PORT` env var injected by Render (value: 10000)

### 10.7 Environment Variables

All sensitive values must be set in the Render dashboard (marked `sync: false` in `render.yaml`) or via dotnet user-secrets locally.

| Variable | Required | Notes |
|----------|---------|-------|
| `DATABASE_URL` | Yes | Postgres URI — auto-injected by Render from linked database |
| `Jwt__Key` | Yes | Min 32 chars; use `Jwt__Key` (double-underscore) format |
| `Jwt__Issuer` | Yes | `cambrian-api` |
| `Jwt__Audience` | Yes | `cambrian-client` |
| `Stripe__SecretKey` | Yes (prod) | `sk_live_...` in production |
| `Stripe__WebhookSecret` | Yes (prod) | `whsec_...` — required for webhook signature verification |
| `Storage__Provider` | Yes (prod) | `s3` or `r2` — `local` forbidden in production |
| `Storage__Endpoint` | Yes (if s3/r2) | S3/R2 endpoint URL |
| `Storage__Bucket` | Yes (if s3/r2) | Bucket name |
| `Storage__AccessKey` | Yes (if s3/r2) | |
| `Storage__SecretKey` | Yes (if s3/r2) | |
| `Storage__PublicUrl` | Yes (if s3/r2) | Base URL for public file access |
| `Email__Provider` | Yes (prod) | `smtp` or `resend` — `console` forbidden in production |
| `Email__ResendApiKey` | Yes (if resend) | Resend API key |
| `Admin__Email` | Recommended | Seeds admin user on first start |
| `Admin__Password` | Recommended | Seeds admin user on first start |
| `App__FrontendUrl` | Yes (prod) | `https://cambrianmusic.com` |
| `App__CorsOrigins` | Yes (prod) | Comma-separated allowed origins |
| `App__VercelProjectSlug` | Optional | For Vercel preview deploy CORS |
| `App__CloudflarePagesSlug` | Optional | For Cloudflare Pages preview CORS |
| `Google__ClientId` | Optional | Required if Google OAuth is needed |
| `SeedDemoUsers__Password` | Optional | Staging only — seeds 10 demo creators |
| `ASPNETCORE_ENVIRONMENT` | Yes | `Production`, `Staging`, or `Development` |
| `PORT` | Yes | Injected by Render; defaults to 8080 locally |

**Local development:** Use `dotnet user-secrets` or `appsettings.Development.json` (never commit secrets).

**Configuration resolution (connection string):**
1. `ConnectionStrings:DefaultConnection` in appsettings
2. `DATABASE_URL` environment variable (Render injects postgres:// URI — auto-converted to Npgsql ADO.NET format)
3. Throws if neither is set (except `Testing` environment)

### 10.8 Rate Limiting

Fixed-window, per-IP:

| Limiter | Production | Staging | Development |
|---------|-----------|---------|-------------|
| Global | 100 req/min | 500 req/min | 500 req/min |
| Auth (`/auth/*`) | 10 req/min | 100 req/min | 200 req/min |
| `api_key_free` (V1 endpoints) | 100 req/min | 100 req/min | 100 req/min |

Configured via `RateLimiting:GlobalPermitLimit` and `RateLimiting:AuthPermitLimit`. Disabled in `Testing` environment (set to `int.MaxValue`).

The `api_key_free` policy partitions by the value of the `X-API-Key` header when present, falling back to remote IP for anonymous requests. Applied via `[EnableRateLimiting("api_key_free")]` on all V1 controllers.

### 10.9 Feature Flags

Active flags (seeded in `20260323030051_SeedCreatorIdentityFeatureFlags`):

| Flag | Purpose |
|------|---------|
| `creator_storefront` | Creator storefront display |
| `creator_profiles` | Creator profile pages |
| `creator_identity` | UUID-based creator identity (new Creator table) |
| `activity_feed` | Activity feed |
| `analytics_capture` | Event tracking capture |
| `trending_v2` | Trending algorithm v2 |
| `checkout_v2` | Checkout UI v2 |
| `sales_ticker` | Real-time sales notifications |

Evaluation: deterministic hash of `(userId, flagName)` checked against `RolloutPercentage`. Same user always gets same result for a given percentage.

---

## 12. Public API v1

**Base URL:** `https://api.cambrianmusic.com`  
**Rate limit policy:** `api_key_free` — 100 req/min per API key (falls back to IP for anonymous)

### Catalogue & Discovery

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| GET | `/api/v1/tracks` | None | `genre`, `mood`, `search`, `tempo`, `instrumental`, `sort`, `page`, `limit` (max 100) |
| GET | `/api/v1/tracks/{id}` | None | Single track with license tier pricing |
| GET | `/api/v1/genres` | None | Distinct genre list |
| GET | `/api/v1/creators/{identifier}` | None | UUID or username slug |

### Licensing

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| POST | `/api/v1/licenses` | Required | Body: `{trackId, licenseType}` — returns `{checkoutUrl}` |
| GET | `/api/v1/licenses/{id}/verify` | None | Public license certificate verification |

### API Key Management

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| POST | `/api/v1/keys` | JWT | Create key — raw key returned once in response |
| GET | `/api/v1/keys` | JWT | List active keys (prefix + metadata, no hashes) |
| DELETE | `/api/v1/keys/{id}` | JWT | Revoke key (soft delete) |

### Frontend Routes (for reference)

| Route | Purpose |
|-------|---------|
| `/developers` | API docs page — auth, endpoints, code examples |
| `/dashboard/api-keys` | Key management — create, view prefix, revoke |

---

## 12. Protected Systems — Requires Explicit Human Approval Before Modification

The following systems require explicit human approval before ANY modification. Do not make changes to these systems even if requested as part of a larger task — stop, document the required change, and wait for confirmation.

| System | Risk | File(s) |
|--------|------|---------|
| **Creator payout flow** | Real money movement; incorrect logic = financial loss or fraud | `StripeWebhookService.cs`, `PayoutService.cs`, `WalletService.cs` |
| **Stripe Connect configuration** | Account type changes affect fee routing and compliance | `StartupExtensions.cs` (ValidateStripeKey), `CreatorConnectService.cs` |
| **Webhook signature verification** | Removing check allows spoofed events and fraudulent payouts | `StripeWebhookService.HandleStripeAsync()` lines 52–73 |
| **Exclusive/copyright buyout atomic SQL** | Race condition protection — EF tracked update is not equivalent | `StripeWebhookService.HandleTrackPurchase()` lines 307–332 |
| **Database migrations (applied)** | Migrations are immutable once applied to any environment | Any file in `Migrations/` with a date in the past |
| **`render.yaml` production section** | Controls live deployment and infrastructure | `render.yaml` lines 120–199 |
| **OpenAPI contract** | Changing the contract breaks the frontend without coordinated deployment | `contracts/openapi.v1.json` |
| **JWT validation configuration** | Weakening validation (e.g. removing issuer check) enables token forgery | `Program.cs` JWT bearer setup |
| **Admin seeding** | Admin password changes affect production access | `StartupExtensions.cs` (SeedAdminAsync) |
| **`TierManifest` fee rates** | Fee rates directly control how much creators are paid per sale | `src/Cambrian.Application/Configuration/TierManifest.cs` |
| **API key hash storage** | `KeyHash` is the only stored form of a key — never add an endpoint that returns `KeyHash` values; key management endpoints must require JWT, not API key auth | `ApiKeyRepository.cs`, `ApiKeysController.cs`, `ApiKeyMiddleware.cs` |
