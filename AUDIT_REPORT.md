# Platform Stability Audit Report

**Generated:** 2026-03-19  
**Scope:** All DTOs (`src/Cambrian.Application/DTOs/`), Domain Entities (`src/Cambrian.Domain/Entities/`), Enums (`src/Cambrian.Domain/Enums/`), DbContext, and Repositories.

---

## Table of Contents

1. [All Enums](#1-all-enums)
2. [All Entities (with full field listing)](#2-all-entities)
3. [All DTOs (with full field listing)](#3-all-dtos)
4. [Orphaned Entities (no DbSet, no repository)](#4-orphaned-entities)
5. [Enum vs String Mismatches](#5-enum-vs-string-mismatches)
6. [Type Mismatches (DTO ↔ Entity)](#6-type-mismatches)
7. [Nullable vs Non-Nullable Mismatches](#7-nullable-vs-non-nullable-mismatches)
8. [Entity Fields NOT Exposed in Any DTO](#8-entity-fields-not-exposed-in-any-dto)
9. [DTO Fields with No Corresponding Entity Field](#9-dto-fields-with-no-corresponding-entity-field)
10. [Missing Validation Annotations](#10-missing-validation-annotations)
11. [Naming Inconsistencies](#11-naming-inconsistencies)
12. [Duplicate/Overlapping Definitions](#12-duplicateoverlapping-definitions)
13. [Structural/Architectural Issues](#13-structuralarchitectural-issues)
14. [Summary of All Issues](#14-summary-of-all-issues)

---

## 1. All Enums

### `UserRole` (`Enums/UserRole.cs`)
| Value | Int |
|-------|-----|
| Listener | 1 |
| Creator | 2 |
| Admin | 3 |

### `LicenseType` (`Enums/LicenseType.cs`)
| Value | Int |
|-------|-----|
| Standard | 1 |
| Extended | 2 |
| Exclusive | 3 |
| CopyrightBuyout | 4 |

### `CreatorTier` (`Enums/CreatorTier.cs`)
| Value | Int |
|-------|-----|
| Free | 0 |
| Pro | 1 |

### `PayoutStatus` (`Enums/PayoutStatus.cs`)
| Value | Int |
|-------|-----|
| Pending | 1 |
| Processing | 2 |
| Paid | 3 |
| Failed | 4 |

### `UsageType` (`Enums/UsageType.cs`)
| Value | Int |
|-------|-----|
| Personal | 1 |
| Youtube | 2 |
| Ads | 3 |
| Podcast | 4 |
| Game | 5 |
| Film | 6 |
| Social | 7 |

---

## 2. All Entities

### `ApplicationUser` (extends `IdentityUser`) — `Entities/ApplicationUser.cs`
| Property | Type | Default | Notes |
|----------|------|---------|-------|
| *(inherited)* Id | string | — | From IdentityUser |
| *(inherited)* Email | string? | — | From IdentityUser |
| *(inherited)* UserName | string? | — | From IdentityUser |
| *(inherited)* PhoneNumber | string? | — | From IdentityUser |
| DisplayName | string? | — | |
| Role | string | "User" | Uses string, not UserRole enum |
| Status | string | "active" | active, suspended |
| Tier | string | "free" | free, paid, creator, pro |
| VerifiedCreator | bool | false | |
| Plan | string? | — | |
| CreatorTier | CreatorTier (enum) | CreatorTier.Free | |
| UploadCount | int | 0 | |
| SubscriptionStatus | string | "Inactive" | Active, Inactive, Cancelled |
| SubscriptionEndDate | DateTime? | — | |
| StripeAccountId | string? | — | |
| WalletBalanceCents | long | 0 | |
| PasswordResetCode | string? | — | |
| PasswordResetCodeExpiry | DateTime? | — | |
| CreatedAt | DateTime | DateTime.UtcNow | |
| Tracks | ICollection\<Track\> | new List | Navigation |
| Purchases | ICollection\<Purchase\> | new List | Navigation |
| Library | ICollection\<LibraryItem\> | new List | Navigation |
| Payouts | ICollection\<Payout\> | new List | Navigation |

**DbSet:** Managed by `IdentityDbContext<ApplicationUser>`.  
**Repositories:** Accessed via `UserManager<ApplicationUser>`.

---

### `User` — `Entities/User.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| Email | string | string.Empty |
| DisplayName | string | string.Empty |
| Role | UserRole (enum) | UserRole.Listener |

**DbSet:** NONE  
**Repository:** NONE  
**Status: ORPHANED** — duplicate of ApplicationUser with incompatible ID type.

---

### `Track` — `Entities/Track.cs`
| Property | Type | Default | Notes |
|----------|------|---------|-------|
| Id | Guid | — | |
| CambrianTrackId | string | "" | Human-readable ID (CAMB-TRK-XXXX) |
| Title | string | "" | |
| Description | string? | — | |
| Genre | string? | — | |
| Mood | string? | — | Search filter |
| Tempo | string? | — | Search filter |
| Instrumental | bool | false | |
| Price | decimal | 0 | |
| Duration | string? | — | |
| LicenseType | string? | — | Uses string, NOT LicenseType enum |
| AudioUrl | string? | — | |
| CoverArtUrl | string? | — | |
| NonExclusivePriceCents | int | 0 | In cents |
| ExclusivePriceCents | int | 0 | In cents |
| CopyrightBuyoutPriceCents | int | 0 | In cents |
| ExclusiveSold | bool | false | |
| Status | string | "available" | available, exclusive_sold, copyright_transferred |
| CopyrightOwnerId | string? | — | |
| CopyrightTransferredAt | DateTime? | — | |
| OriginalCreatorId | string? | — | |
| Visibility | string | "public" | public, limited, hidden |
| CreatedAt | DateTime | DateTime.UtcNow | |
| CreatorId | string | "" | FK to ApplicationUser |
| Creator | ApplicationUser | null! | Navigation |
| Tags | ICollection\<string\> | new List | Stored as CSV |
| Purchases | ICollection\<Purchase\> | new List | Navigation |
| LibraryItems | ICollection\<LibraryItem\> | new List | Navigation |

**DbSet:** `Tracks`  
**Repository:** `ITrackRepository` / `TrackRepository`

---

### `Purchase` — `Entities/Purchase.cs`
| Property | Type | Default | Notes |
|----------|------|---------|-------|
| Id | Guid | — | |
| BuyerId | string | "" | FK to ApplicationUser |
| Buyer | ApplicationUser | null! | Navigation |
| TrackId | Guid | — | FK to Track |
| Track | Track | null! | Navigation |
| AmountCents | int | 0 | In cents |
| PaymentMethod | string? | — | |
| LicenseType | string? | — | Uses string, NOT LicenseType enum |
| Status | string | "pending" | pending, completed, refunded |
| UsageType | string | "personal" | Uses string, NOT UsageType enum |
| StripeSessionId | string? | — | |
| LicenseId | Guid? | — | FK to LicenseCertificate |
| License | LicenseCertificate? | — | Navigation |
| CompletedAt | DateTime? | — | |
| ExpiresAt | DateTime? | — | |
| CreatedAt | DateTime | DateTime.UtcNow | |
| UpdatedAt | DateTime? | — | |

**DbSet:** `Purchases`  
**Repository:** `IPurchaseRepository` / `PurchaseRepository`

---

### `LibraryItem` — `Entities/LibraryItem.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| UserId | string | "" |
| User | ApplicationUser | null! |
| TrackId | Guid | — |
| Track | Track | null! |
| PurchaseId | Guid? | — |
| Purchase | Purchase? | — |
| Title | string? | — |
| Artist | string? | — |
| AudioUrl | string? | — |
| SavedAt | DateTime | DateTime.UtcNow |

**DbSet:** `Library`  
**Repository:** `ILibraryRepository` / `LibraryRepository`

---

### `Payout` — `Entities/Payout.cs`
| Property | Type | Default | Notes |
|----------|------|---------|-------|
| Id | Guid | — | |
| CreatorId | string | "" | FK to ApplicationUser |
| Creator | ApplicationUser | null! | Navigation |
| AmountCents | int | 0 | In cents |
| Status | string | "pending" | Uses string, NOT PayoutStatus enum |
| RequestedAt | DateTime | DateTime.UtcNow | |
| CompletedAt | DateTime? | — | |

**DbSet:** `Payouts`  
**Repository:** `IPayoutRepository` / `PayoutRepository`

---

### `Subscription` — `Entities/Subscription.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| UserId | string | "" |
| User | ApplicationUser | null! |
| Plan | string | "free" |
| Status | string | "active" |
| StartedAt | DateTime | DateTime.UtcNow |
| ExpiresAt | DateTime? | — |

**DbSet:** `Subscriptions`  
**Repository:** `ISubscriptionRepository` / `SubscriptionRepository`

---

### `Invoice` — `Entities/Invoice.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| UserId | string | "" |
| User | ApplicationUser | null! |
| PurchaseId | Guid | — |
| Purchase | Purchase | null! |
| AmountCents | int | 0 |
| Currency | string | "usd" |
| Status | string | "issued" |
| IssuedAt | DateTime | DateTime.UtcNow |
| PaidAt | DateTime? | — |

**DbSet:** `Invoices`  
**Repository:** `IInvoiceRepository` / `InvoiceRepository`

---

### `WalletTransaction` — `Entities/WalletTransaction.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| UserId | string | "" |
| User | ApplicationUser | null! |
| AmountCents | long | 0 |
| Type | string | "" |
| Description | string? | — |
| RelatedPurchaseId | Guid? | — |
| CreatedAt | DateTime | DateTime.UtcNow |

**DbSet:** `WalletTransactions`  
**Repository:** `IWalletRepository` / `WalletRepository`

---

### `LicenseCertificate` — `Entities/LicenseCertificate.cs`
| Property | Type | Default | Notes |
|----------|------|---------|-------|
| Id | Guid | — | |
| TrackId | string | "" | CambrianTrackId, NOT Guid FK |
| BuyerId | string | "" | |
| Buyer | ApplicationUser | null! | Navigation |
| CreatorId | string | "" | |
| Creator | ApplicationUser | null! | Navigation |
| PurchaseId | Guid | — | |
| Purchase | Purchase | null! | Navigation |
| LicenseType | string | "non-exclusive" | Uses string, NOT enum |
| UsageType | string | "personal" | Uses string, NOT enum |
| IssuedAt | DateTime | DateTime.UtcNow | |
| AllowedUses | List\<string\>? | — | Stored as CSV |
| Restrictions | List\<string\>? | — | Stored as CSV |
| CopyrightOwner | string? | — | |

**DbSet:** `LicenseCertificates`  
**Repository:** `ILicenseCertificateRepository` / `LicenseCertificateRepository`

---

### `CreatorProfile` — `Entities/CreatorProfile.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| UserId | string | "" |
| Slug | string | "" |
| BannerImageUrl | string? | — |
| ProfileImageUrl | string? | — |
| Bio | string | "" |
| Niche | string? | — |
| SocialLinks | string? | — |
| ShowEarnings | bool | false |
| ShowDownloadStats | bool | false |
| PinnedTrackIds | string? | — |
| CreatedAt | DateTime | DateTime.UtcNow |
| UpdatedAt | DateTime | DateTime.UtcNow |

**DbSet:** `CreatorProfiles`  
**Repository:** `ICreatorProfileRepository` / `CreatorProfileRepository`

---

### `TrackCollection` — `Entities/TrackCollection.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| CreatorId | string | "" |
| Title | string | "" |
| Description | string? | — |
| CoverImageUrl | string? | — |
| TrackIds | string | "" |
| CreatedAt | DateTime | DateTime.UtcNow |
| UpdatedAt | DateTime | DateTime.UtcNow |

**DbSet:** `TrackCollections`  
**Repository:** `ICreatorProfileRepository` (shared)

---

### `AuditLog` — `Entities/AuditLog.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| Action | string | "" |
| Admin | string | "" |
| Timestamp | DateTime | DateTime.UtcNow |
| Details | string? | — |

**DbSet:** `AuditLogs`  
**Repository:** `IAdminRepository` (shared)

---

### `AbuseReport` — `Entities/AbuseReport.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| TrackId | Guid | — |
| Track | Track | null! |
| Reason | string | "" |
| Status | string | "open" |
| ReportedAt | DateTime | DateTime.UtcNow |
| ReportedByUserId | string? | — |

**DbSet:** `AbuseReports`  
**Repository:** `IAdminRepository` (shared)

---

### `StreamSession` — `Entities/StreamSession.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| TrackId | Guid | — |
| Track | Track | null! |
| UserId | string? | — |
| Title | string? | — |
| StartedAt | DateTime | DateTime.UtcNow |
| StoppedAt | DateTime? | — |

**DbSet:** `StreamSessions`  
**Repository:** `IStreamRepository` / `StreamRepository`

---

### `StripeWebhookEvent` — `Entities/StripeWebhookEvent.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| EventId | string | "" |
| EventType | string | "" |
| Processed | bool | false |
| Payload | string? | — |
| ProcessedAt | DateTime | DateTime.UtcNow |

**DbSet:** `StripeWebhookEvents`  
**Repository:** Accessed directly via DbContext

---

### `AnalyticsEvent` — `Entities/AnalyticsEvent.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| EventType | string | "" |
| UserId | string? | — |
| TrackId | Guid? | — |
| Metadata | string? | — |
| CreatedAt | DateTime | DateTime.UtcNow |

**DbSet:** `AnalyticsEvents`  
**Repository:** `IAnalyticsRepository` / `AnalyticsRepository`

---

### `FeatureFlag` — `Entities/FeatureFlag.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| Name | string | "" |
| Enabled | bool | false |
| RolloutPercentage | int | 100 |
| CreatedAt | DateTime | DateTime.UtcNow |
| UpdatedAt | DateTime | DateTime.UtcNow |

**DbSet:** `FeatureFlags`  
**Repository:** `IFeatureFlagRepository` / `FeatureFlagRepository`

---

### `CreatorBalance` — `Entities/CreatorBalance.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| CreatorId | Guid | — |
| AvailableAmount | decimal | 0 |

**DbSet:** NONE  
**Repository:** NONE  
**Status: ORPHANED**

---

### `TrackFile` — `Entities/TrackFile.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| TrackId | Guid | — |
| FileName | string | string.Empty |
| StorageKey | string | string.Empty |

**DbSet:** NONE  
**Repository:** NONE  
**Status: ORPHANED** — no navigation property to Track either.

---

### `License` — `Entities/License.cs`
| Property | Type | Default | Notes |
|----------|------|---------|-------|
| Id | Guid | — | |
| TrackId | Guid | — | |
| Type | LicenseType (enum) | LicenseType.Standard | Only entity using this enum |
| Price | decimal | 0 | |

**DbSet:** NONE  
**Repository:** NONE  
**Status: ORPHANED** — overlaps with LicenseCertificate.

---

### `ModerationAction` — `Entities/ModerationAction.cs`
| Property | Type | Default | Notes |
|----------|------|---------|-------|
| Id | Guid | — | |
| TargetId | Guid | — | |
| Action | string | string.Empty | |
| Reason | string | string.Empty | |
| CreatedAt | DateTimeOffset | DateTimeOffset.UtcNow | Uses DateTimeOffset, not DateTime |

**DbSet:** NONE  
**Repository:** NONE  
**Status: ORPHANED**

---

### `Payment` — `Entities/Payment.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| PurchaseId | Guid | — |
| ProviderReference | string | string.Empty |
| Status | string | "pending" |

**DbSet:** NONE  
**Repository:** NONE  
**Status: ORPHANED** — no navigation properties, appears to be an unused stub.

---

## 3. All DTOs

### Auth DTOs

#### `LoginRequest` — `DTOs/Auth/LoginRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| Email | string | [Required], [EmailAddress] |
| Password | string | [Required], [MinLength(8)] |

#### `RegisterRequest` — `DTOs/Auth/RegisterRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| Email | string | [Required], [EmailAddress] |
| Password | string | [Required], [MinLength(8)], [RegularExpression] |
| DisplayName | string? | [MaxLength(100)] |
| Role | string? | [RegularExpression("^(user|creator)$")] |

#### `AuthResponse` — `DTOs/Auth/AuthResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| UserId | Guid | — |
| Email | string | string.Empty |
| Token | string | string.Empty |
| Tier | string | "free" |
| Role | string | "User" |

#### `UserProfileResponse` — `DTOs/Auth/UserProfileResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| UserId | string | string.Empty |
| Email | string | string.Empty |
| DisplayName | string | string.Empty |
| Role | string | string.Empty |
| Tier | string | string.Empty |
| VerifiedCreator | bool | false |
| CreatorTier | string | "Free" |
| UploadCount | int | 0 |
| UploadLimit | int? | — |
| SubscriptionStatus | string | "Inactive" |
| SubscriptionEndDate | DateTime? | — |
| PlatformFeePercent | decimal | 0 |
| ContractVersion | string | "1.0.0" |

#### `ChangePasswordRequest` — `DTOs/Auth/ChangePasswordRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| CurrentPassword | string | [Required] |
| NewPassword | string | [Required], [MinLength(8)] |

#### `ChangeEmailRequest` — `DTOs/Auth/ChangeEmailRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| Password | string | [Required] |
| NewEmail | string | [Required], [EmailAddress] |

#### `ForgotPasswordRequest` — `DTOs/Auth/ForgotPasswordRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| Email | string? | NONE |
| PhoneNumber | string? | NONE |

#### `VerifyCodeRequest` — `DTOs/Auth/VerifyCodeRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| Email | string? | NONE |
| PhoneNumber | string? | NONE |
| Code | string | [Required], [StringLength(6, Min=6)] |

#### `ResetPasswordRequest` — `DTOs/Auth/ResetPasswordRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| Email | string? | NONE |
| PhoneNumber | string? | NONE |
| Code | string | [Required], [StringLength(6, Min=6)] |
| NewPassword | string | [Required], [MinLength(8)] |

#### `RecoverUsernameRequest` — `DTOs/Auth/RecoverUsernameRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| Email | string? | NONE |
| PhoneNumber | string? | NONE |

---

### Catalog DTOs

#### `TrackResponse` — `DTOs/Catalog/TrackResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | string | Guid.NewGuid().ToString() |
| CambrianTrackId | string | string.Empty |
| Title | string | string.Empty |
| Description | string? | — |
| Genre | string | string.Empty |
| Price | decimal | 0 |
| NonExclusivePrice | decimal | 0 |
| ExclusivePrice | decimal | 0 |
| CopyrightBuyoutPrice | decimal | 0 |
| PlatformFeePercent | decimal | 0.15m |
| NonExclusivePlatformFee | decimal | 0 |
| NonExclusiveCreatorEarnings | decimal | 0 |
| ExclusivePlatformFee | decimal | 0 |
| ExclusiveCreatorEarnings | decimal | 0 |
| CopyrightBuyoutPlatformFee | decimal | 0 |
| CopyrightBuyoutCreatorEarnings | decimal | 0 |
| ExclusiveSold | bool | false |
| Status | string | "available" |
| CopyrightOwnerId | string? | — |
| LicenseType | string? | — |
| Duration | string? | — |
| AudioUrl | string? | — |
| CoverArtUrl | string? | — |
| CreatorId | string | string.Empty |
| Artist | string? | — |
| CreatedAt | DateTime | — |

#### `UploadTrackRequest` — `DTOs/Catalog/UploadTrackRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| Audio | IFormFile | [Required] |
| CoverArt | IFormFile? | NONE |
| Title | string | [Required], [MaxLength(200)] |
| Description | string? | [MaxLength(2000)] |
| Genre | string? | [MaxLength(60)] |
| Price | decimal? | NONE |
| LicenseType | string? | NONE |
| Tags | string? | NONE |
| NonExclusivePrice | decimal? | NONE |
| ExclusivePrice | decimal? | NONE |
| CopyrightBuyoutPrice | decimal? | NONE |
| CreatorId | string? | NONE |

#### `UploadTrackResponse` — `DTOs/Catalog/UploadTrackResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| TrackId | string | string.Empty |
| Title | string | string.Empty |
| CambrianTrackId | string | string.Empty |

#### `TrackIdDto` — `DTOs/Catalog/TrackIdDto.cs`
| Property | Type | Validation |
|----------|------|------------|
| TrackId | string | [Required], [RegularExpression] |

#### `CatalogSearchFilters` — `DTOs/Catalog/CatalogSearchFilters.cs`
| Property | Type | Default |
|----------|------|---------|
| Genre | string? | — |
| Search | string? | — |
| Sort | string? | — |
| Mood | string? | — |
| Tempo | string? | — |
| Instrumental | bool? | — |
| Duration | string? | — |
| Page | int | 1 |
| PageSize | int | 20 |

#### `PagedResult<T>` — `DTOs/Catalog/PagedResult.cs`
| Property | Type |
|----------|------|
| Items | IReadOnlyCollection\<T\> |
| Page | int |
| PageSize | int |
| TotalCount | int |
| TotalPages | int (computed) |
| HasNextPage | bool (computed) |
| HasPreviousPage | bool (computed) |

---

### Checkout DTOs

#### `CheckoutRequest` — `DTOs/Checkout/CheckoutRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| TrackId | string | [Required] |
| LicenseType | string | [Required], [RegularExpression] |
| ClientReferenceId | string? | NONE |
| UsageType | string? | [RegularExpression] |

#### `CheckoutResponse` — `DTOs/Checkout/CheckoutResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| CheckoutUrl | string | string.Empty |
| Status | string | "created" |
| LicenseCertificate | LicenseCertificateDto? | — |

#### `CheckoutConfirmResponse` — `DTOs/Checkout/CheckoutConfirmResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| Status | string | "pending" |
| TrackId | string? | — |
| LicenseType | string? | — |
| AddedToLibrary | bool | false |
| SessionId | string | "" |
| LicenseId | string? | — |

---

### Billing DTOs

#### `BillingCheckoutRequest` — `DTOs/Billing/BillingCheckoutRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| Tier | string? | NONE |

#### `CheckoutResponse` — `DTOs/Billing/CheckoutResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| CheckoutUrl | string | string.Empty |

#### `CheckoutSessionStatusResponse` — `DTOs/Billing/CheckoutSessionStatusResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| Status | string | "pending" |
| Tier | string? | — |
| SessionId | string | "" |

#### `BillingStatusResponse` — `DTOs/Billing/BillingStatusResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| Tier | string | "free" |
| Status | string | "active" |
| ExpiresAt | DateTime? | — |
| CreatorTier | string | "Free" |
| UploadCount | int | 0 |
| UploadLimit | int? | — |
| PlatformFeePercent | decimal | 0 |

---

### Purchase DTOs

#### `PurchaseCreateRequest` — `DTOs/Purchases/PurchaseCreateRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| TrackId | string | NONE (has default "") |
| LicenseType | string? | NONE |
| PaymentMethod | string? | NONE |
| StripeSessionId | string? | NONE |
| UsageType | string? | NONE |

#### `PurchaseResponse` — `DTOs/Purchases/PurchaseResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | string | "" |
| TrackId | string | "" |
| TrackTitle | string | "" |
| AmountCents | int | 0 |
| Currency | string | "usd" |
| LicenseType | string | "non-exclusive" |
| Status | string | "pending" |
| CreatedAt | DateTime | — |
| CompletedAt | DateTime? | — |

#### `CreditCreatorRequest` — `DTOs/Purchases/CreditCreatorRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| CreatorId | string? | NONE |
| TrackId | string? | NONE |
| TrackTitle | string? | NONE |
| AmountCents | int | NONE |
| LicenseType | string? | NONE |

---

### Subscription DTOs

#### `UpdateSubscriptionRequest` — `DTOs/Subscriptions/UpdateSubscriptionRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| Plan | string | [Required], [RegularExpression("^(free|paid|creator)$")] |

#### `SubscriptionResponse` — `DTOs/Subscriptions/SubscriptionResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | Guid | — |
| Plan | string | "free" |
| Status | string | "active" |
| StartedAt | DateTime | — |
| ExpiresAt | DateTime? | — |

#### `PlanResponse` — `DTOs/Subscriptions/PlanResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| Name | string | "" |
| Description | string | "" |
| PriceCents | int | 0 |
| Interval | string | "month" |
| Features | List\<string\> | [] |

---

### Payout DTOs

#### `PayoutRequest` — `DTOs/Payouts/PayoutRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| Amount | decimal | NONE |

#### `PayoutResponse` — `DTOs/Payouts/PayoutResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| Amount | decimal | 0 |
| Status | string | "pending" |

---

### Wallet DTOs

#### `WalletResponse` — `DTOs/Wallet/WalletResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| BalanceCents | long | 0 |
| Currency | string | "usd" |

#### `WalletTransactionResponse` — `DTOs/Wallet/WalletTransactionResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | string | "" |
| AmountCents | long | 0 |
| Type | string | "" |
| Description | string? | — |
| CreatedAt | DateTime | — |

#### `WithdrawRequest` — `DTOs/Wallet/WithdrawRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| Amount | decimal | NONE |

---

### Library DTOs

#### `LibraryItemResponse` — `DTOs/Library/LibraryItemResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| TrackId | string | string.Empty |
| Id | string | (computed: => TrackId) |
| Title | string | string.Empty |
| Artist | string | string.Empty |
| Purchased | bool | false |
| PurchasedOn | string? | — |
| AudioUrl | string? | — |
| Genre | string? | — |

#### `LibrarySaveRequest` — `DTOs/Library/LibrarySaveRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| TrackId | string | [Required] |
| Title | string? | NONE |
| Artist | string? | NONE |
| AudioUrl | string? | NONE |

---

### Invoice DTOs

#### `InvoiceResponse` — `DTOs/Invoices/InvoiceResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | string | "" |
| PurchaseId | string | "" |
| AmountCents | int | 0 |
| Currency | string | "usd" |
| Status | string | "issued" |
| IssuedAt | DateTime | — |
| PaidAt | DateTime? | — |

---

### License DTOs

#### `LicenseCertificateDto` — `DTOs/Licenses/LicenseCertificateDto.cs`
| Property | Type | Default |
|----------|------|---------|
| LicenseId | string | string.Empty |
| TrackId | string | string.Empty |
| LicenseType | string | string.Empty |
| BuyerId | string | string.Empty |
| CreatorId | string | string.Empty |
| UsageType | string? | — |
| IssuedAt | DateTime | — |
| AllowedUses | List\<string\>? | — |
| Restrictions | List\<string\>? | — |
| CopyrightOwner | string? | — |

---

### CreatorProfile DTOs

#### `CreatorProfileDto` — `DTOs/CreatorProfile/CreatorProfileDto.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | string | "" |
| UserId | string | "" |
| Slug | string | "" |
| Bio | string | "" |
| Niche | string? | — |
| ProfileImageUrl | string? | — |
| BannerImageUrl | string? | — |
| SocialLinks | List\<SocialLinkDto\>? | — |
| Stats | CreatorStatsDto | new() |
| ShowEarnings | bool | false |
| ShowDownloadStats | bool | false |
| PinnedTrackIds | string? | — |
| CreatedAt | DateTime | — |
| UpdatedAt | DateTime | — |

#### `UpsertCreatorProfileRequest` — `DTOs/CreatorProfile/UpsertCreatorProfileRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| Slug | string? | NONE |
| Bio | string? | NONE |
| Niche | string? | NONE |
| SocialLinks | List\<SocialLinkDto\>? | NONE |
| ShowEarnings | bool | NONE |
| ShowDownloadStats | bool | NONE |

#### `CreatorStatsDto` — `DTOs/CreatorProfile/CreatorStatsDto.cs`
| Property | Type |
|----------|------|
| TotalDownloads | int |
| TotalEarnings | decimal |

#### `SocialLinkDto` — `DTOs/CreatorProfile/SocialLinkDto.cs`
| Property | Type |
|----------|------|
| Platform | string |
| Url | string |

#### `TrackCollectionDto` — `DTOs/CreatorProfile/TrackCollectionDto.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | string | "" |
| Title | string | "" |
| Description | string? | — |
| CoverImageUrl | string? | — |
| TrackIds | string | "" |
| CreatedAt | DateTime | — |
| UpdatedAt | DateTime | — |

#### `UpsertCollectionRequest` — `DTOs/CreatorProfile/UpsertCollectionRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| Title | string? | NONE |
| Description | string? | NONE |
| TrackIds | string? | NONE |

#### `UpdatePinnedTracksRequest` — `DTOs/CreatorProfile/UpdatePinnedTracksRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| TrackIds | string? | NONE |

#### `StorefrontResponse` — `DTOs/CreatorProfile/StorefrontResponse.cs`
| Property | Type |
|----------|------|
| Profile | CreatorProfileDto |
| Stats | CreatorStatsDto |
| PinnedTracks | IReadOnlyList\<TrackResponse\> |
| Collections | IReadOnlyList\<TrackCollectionDto\> |
| Tracks | IReadOnlyList\<TrackResponse\> |

---

### Payment DTOs

#### `PaymentCheckoutRequest` — `DTOs/Payments/PaymentCheckoutRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| TrackId | string? | NONE |
| LicenseType | string | NONE (default "non-exclusive") |
| UsageType | string | NONE (default "personal") |

#### `PaymentCheckoutResponse` — `DTOs/Payments/PaymentCheckoutResponse.cs`
| Property | Type |
|----------|------|
| CheckoutUrl | string? |

#### `PaymentProcessRequest` — `DTOs/Payments/PaymentProcessRequest.cs`
| Property | Type | Validation |
|----------|------|------------|
| PurchaseId | string | [Required] |
| PaymentMethodId | string? | NONE |

#### `PaymentResultResponse` — `DTOs/Payments/PaymentResultResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| Status | string | "pending" |
| PurchaseId | string? | — |
| EventId | string? | — |
| EventType | string? | — |
| Duplicate | bool | false |

#### `PaymentStateResponse` — `DTOs/Payments/PaymentStateResponse.cs`
| Property | Type | Default |
|----------|------|---------|
| Status | string | "pending" |
| PurchaseIds | List\<string\> | [] |
| ProcessedEventIds | List\<string\> | [] |

---

### Admin DTOs

#### `AdminUser` — `DTOs/Admin/AdminUser.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | string | string.Empty |
| Email | string | string.Empty |
| Role | string | "User" |
| Status | string | "active" |
| Tier | string | "free" |
| VerifiedCreator | bool | false |

#### `AdminAuditLog` — `DTOs/Admin/AdminAuditLog.cs`
| Property | Type | Default |
|----------|------|---------|
| Id | string | string.Empty |
| Action | string | string.Empty |
| Admin | string | string.Empty |
| Timestamp | DateTime | — |
| Details | string? | — |

#### `AdminDashboardSummary` — `DTOs/Admin/AdminDashboardSummary.cs`
| Property | Type |
|----------|------|
| TotalUsers | double |
| ActiveCreators | double |
| TracksUploaded | double |
| LicensesSold | double |
| TotalRevenue | double |
| PendingPayouts | double |

#### `PurgeResult` — `DTOs/Admin/PurgeResult.cs`
| Property | Type |
|----------|------|
| UsersDeleted | int |
| TracksDeleted | int |
| PurchasesDeleted | int |
| LibraryItemsDeleted | int |
| InvoicesDeleted | int |
| PayoutsDeleted | int |
| SubscriptionsDeleted | int |
| StreamSessionsDeleted | int |
| WalletTransactionsDeleted | int |
| WebhookEventsDeleted | int |
| AuditLogsDeleted | int |
| AbuseReportsDeleted | int |
| LicenseCertificatesDeleted | int |
| AdminPreserved | string |

#### `IntegrityReport` — `DTOs/Admin/IntegrityReport.cs`
| Property | Type |
|----------|------|
| GeneratedAt | DateTime |
| TotalViolations | int (computed) |
| Violations | List\<IntegrityViolation\> |
| Summary | IntegritySummary |

#### `IntegrityViolation` — `DTOs/Admin/IntegrityReport.cs`
| Property | Type |
|----------|------|
| Rule | string |
| Severity | string |
| EntityType | string |
| EntityId | string |
| Description | string |

#### `IntegritySummary` — `DTOs/Admin/IntegrityReport.cs`
| Property | Type |
|----------|------|
| CompletedPurchasesWithoutLibrary | int |
| ExclusiveSoldButBrowsable | int |
| PayoutAmountMismatches | int |
| OrphanedLibraryItems | int |
| PurchasesWithoutInvoice | int |
| ExclusivePurchasesWithoutFlag | int |

---

## 4. Orphaned Entities

These entities exist in `Cambrian.Domain.Entities` but have **no DbSet** in `CambrianDbContext` and **no repository**:

| Entity | Issue | Risk |
|--------|-------|------|
| **User** | Duplicate of ApplicationUser with incompatible `Guid` Id (vs Identity `string` Id). Uses `UserRole` enum while ApplicationUser uses `string`. | Dead code. Confusing for developers. Enum `UserRole` is only used here. |
| **CreatorBalance** | `CreatorId` is `Guid` (inconsistent with the `string` FK convention used everywhere else). No DbSet, no repository, no service. | Dead code. `ApplicationUser.WalletBalanceCents` serves this purpose. |
| **TrackFile** | No navigation property to `Track`, no DbSet, no repository. | Dead code. File management appears to use cloud storage URLs directly on `Track.AudioUrl`. |
| **License** | Only entity using `LicenseType` enum. Overlaps with `LicenseCertificate` which uses string-typed LicenseType. No DbSet, no repository. | Dead code. `LicenseCertificate` is the active entity for licenses. |
| **ModerationAction** | No DbSet, no repository, no service. Uses `DateTimeOffset` (unique in the codebase). | Dead code. Moderation may use `AbuseReport` + `AuditLog` instead. |
| **Payment** | Stub entity with only 4 properties, no navigation properties, no DbSet, no repository. | Dead code. Payment processing uses `Purchase.StripeSessionId` and `StripeWebhookEvent`. |

---

## 5. Enum vs String Mismatches

These are the most dangerous class of bugs — defined enums exist but entities/DTOs use raw strings instead:

| Location | Field | Actual Type | Enum Available | Risk |
|----------|-------|-------------|----------------|------|
| `ApplicationUser.Role` | Role | `string` ("User", "Admin") | `UserRole` (Listener, Creator, Admin) | Enum values don't match string values ("User" vs "Listener"). Enum is only used by orphaned `User` entity. |
| `ApplicationUser.Tier` | Tier | `string` ("free", "paid", "creator", "pro") | `CreatorTier` (Free, Pro) | `Tier` has 4 values but `CreatorTier` enum only has 2. These are semantically different concepts sharing confusing names. |
| `ApplicationUser.CreatorTier` | CreatorTier | `CreatorTier` enum | ✓ used correctly | OK, but coexists with string `Tier` field — confusing. |
| `Payout.Status` | Status | `string` ("pending", "approved", "rejected", "completed") | `PayoutStatus` (Pending, Processing, Paid, Failed) | **Values don't match.** Entity uses "approved"/"rejected"/"completed"; enum uses "Processing"/"Paid"/"Failed". |
| `Purchase.LicenseType` | LicenseType | `string?` | `LicenseType` (Standard, Extended, Exclusive, CopyrightBuyout) | Enum exists but is unused. String values in practice are "standard", "non-exclusive", "exclusive", "copyright_buyout" — **"non-exclusive" has no enum member**, "Extended" has no string usage. |
| `Purchase.UsageType` | UsageType | `string` ("personal") | `UsageType` (Personal, Youtube, Ads, ...) | Enum exists but is unused. |
| `Track.LicenseType` | LicenseType | `string?` | `LicenseType` enum | Enum unused. |
| `LicenseCertificate.LicenseType` | LicenseType | `string` ("non-exclusive") | `LicenseType` enum | Enum unused. "non-exclusive" has no enum member. |
| `LicenseCertificate.UsageType` | UsageType | `string` ("personal") | `UsageType` enum | Enum unused. |
| `UserProfileResponse.CreatorTier` | CreatorTier | `string` ("Free") | `CreatorTier` enum | DTO converts enum to string for JSON. Acceptable but adds a manual serialization step. |
| `BillingStatusResponse.CreatorTier` | CreatorTier | `string` ("Free") | `CreatorTier` enum | Same as above. |

**Key finding:** The `LicenseType` enum defines `Extended` (no string usage) but omits `NonExclusive` (used extensively as the string "non-exclusive"). The `PayoutStatus` enum values don't match the string values used in the `Payout` entity at all.

---

## 6. Type Mismatches (DTO ↔ Entity)

| DTO Field | DTO Type | Entity Field | Entity Type | Issue |
|-----------|----------|--------------|-------------|-------|
| `AuthResponse.UserId` | **Guid** | `ApplicationUser.Id` | **string** | Guid vs string. Will throw at runtime if ID isn't a valid GUID. |
| `AdminAuditLog.Id` | **string** | `AuditLog.Id` | **Guid** | String representation of Guid. Minor but inconsistent with `SubscriptionResponse.Id` which is `Guid`. |
| `WalletTransactionResponse.Id` | **string** | `WalletTransaction.Id` | **Guid** | Same pattern. |
| `PurchaseResponse.Id` | **string** | `Purchase.Id` | **Guid** | Same pattern. |
| `PurchaseResponse.TrackId` | **string** | `Purchase.TrackId` | **Guid** | Same pattern. |
| `InvoiceResponse.Id` | **string** | `Invoice.Id` | **Guid** | Same pattern. |
| `InvoiceResponse.PurchaseId` | **string** | `Invoice.PurchaseId` | **Guid** | Same pattern. |
| `CreatorProfileDto.Id` | **string** | `CreatorProfile.Id` | **Guid** | Same pattern. |
| `TrackCollectionDto.Id` | **string** | `TrackCollection.Id` | **Guid** | Same pattern. |
| `TrackResponse.Id` | **string** | `Track.Id` | **Guid** | Same pattern. Worse: defaults to `Guid.NewGuid().ToString()` in DTO constructor. |
| `SubscriptionResponse.Id` | **Guid** | `Subscription.Id` | **Guid** | **Consistent** — but inconsistent with all other response DTOs using string. |
| `PayoutResponse.Amount` | **decimal** (dollars) | `Payout.AmountCents` | **int** (cents) | **Unit mismatch.** Requires cents→dollars conversion. Easy to forget / get wrong. |
| `PayoutRequest.Amount` | **decimal** (dollars) | `Payout.AmountCents` | **int** (cents) | Same unit mismatch. |
| `WithdrawRequest.Amount` | **decimal** (dollars) | `ApplicationUser.WalletBalanceCents` | **long** (cents) | Same unit mismatch. |
| `TrackResponse.NonExclusivePrice` | **decimal** (dollars) | `Track.NonExclusivePriceCents` | **int** (cents) | Different name AND unit. |
| `TrackResponse.ExclusivePrice` | **decimal** (dollars) | `Track.ExclusivePriceCents` | **int** (cents) | Different name AND unit. |
| `TrackResponse.CopyrightBuyoutPrice` | **decimal** (dollars) | `Track.CopyrightBuyoutPriceCents` | **int** (cents) | Different name AND unit. |
| `AdminDashboardSummary.*` | **double** | N/A (computed) | N/A | `double` for integer counts (TotalUsers, TracksUploaded) is wrong — should be `int` or `long`. |
| `LicenseCertificateDto.LicenseId` | **string** | `LicenseCertificate.Id` | **Guid** | Field name also differs (LicenseId vs Id). |

---

## 7. Nullable vs Non-Nullable Mismatches

| DTO Field | DTO Nullability | Entity Field | Entity Nullability | Issue |
|-----------|-----------------|--------------|--------------------|----|
| `TrackResponse.Genre` | `string` (non-null) | `Track.Genre` | `string?` (nullable) | DTO forces empty string when entity is null. May hide missing data. |
| `UserProfileResponse.DisplayName` | `string` (non-null) | `ApplicationUser.DisplayName` | `string?` (nullable) | Same issue. |
| `PurchaseResponse.LicenseType` | `string` (non-null, default "non-exclusive") | `Purchase.LicenseType` | `string?` (nullable) | DTO silently converts null to "non-exclusive". |
| `LibraryItemResponse.Title` | `string` (non-null) | `LibraryItem.Title` | `string?` (nullable) | Null→empty conversion. |
| `LibraryItemResponse.Artist` | `string` (non-null) | `LibraryItem.Artist` | `string?` (nullable) | Null→empty conversion. |
| `LicenseCertificateDto.UsageType` | `string?` (nullable) | `LicenseCertificate.UsageType` | `string` (non-null, default "personal") | Reversed: DTO is more permissive than entity. |
| `PaymentCheckoutRequest.TrackId` | `string?` (nullable) | — | — | Should be required for a checkout request. |
| `CreditCreatorRequest.CreatorId` | `string?` (nullable) | — | — | Should be required to credit a creator. |
| `CreditCreatorRequest.TrackId` | `string?` (nullable) | — | — | Should be required. |
| `ForgotPasswordRequest.Email` | `string?` | — | — | Both Email and PhoneNumber are nullable — can submit an empty request. |
| `RecoverUsernameRequest.Email` | `string?` | — | — | Same issue. |

---

## 8. Entity Fields NOT Exposed in Any DTO

These entity fields have no corresponding property in any response DTO, meaning they are invisible to the API:

### Track (6 unexposed fields)
| Field | Type | Impact |
|-------|------|--------|
| `Mood` | string? | Cannot search/display mood in frontend despite CatalogSearchFilters supporting Mood filter |
| `Tempo` | string? | Same — filter exists but value never returned |
| `Instrumental` | bool | Same — filter exists but value never returned |
| `Tags` | ICollection\<string\> | Tags uploaded but never returned in TrackResponse |
| `CopyrightTransferredAt` | DateTime? | Copyright transfer timestamp invisible |
| `OriginalCreatorId` | string? | Original creator invisible after copyright transfer |
| `Visibility` | string | Track visibility not returned (public/limited/hidden) |

### Purchase (7 unexposed fields)
| Field | Type | Impact |
|-------|------|--------|
| `BuyerId` | string | Buyer identity not in PurchaseResponse |
| `PaymentMethod` | string? | Payment method not exposed |
| `UsageType` | string | License usage context not in purchase response |
| `StripeSessionId` | string? | Internal, acceptable |
| `LicenseId` | Guid? | License reference not in purchase response |
| `ExpiresAt` | DateTime? | Pending purchase expiry not visible |
| `UpdatedAt` | DateTime? | Status change timestamp not visible |

### ApplicationUser (5 unexposed fields)
| Field | Type | Impact |
|-------|------|--------|
| `Plan` | string? | Plan field exists but is never exposed in any DTO |
| `StripeAccountId` | string? | Stripe account not exposed (may be intentional) |
| `WalletBalanceCents` | long | Only exposed via WalletResponse, not in UserProfile |
| `CreatedAt` | DateTime | Account creation date not in UserProfileResponse |
| `Status` | string | Only in AdminUser DTO, not in UserProfileResponse |

### Payout (4 unexposed fields in PayoutResponse)
| Field | Type | Impact |
|-------|------|--------|
| `Id` | Guid | Cannot reference specific payout |
| `CreatorId` | string | Creator identity not returned |
| `RequestedAt` | DateTime | When payout was requested not visible |
| `CompletedAt` | DateTime? | When payout completed not visible |

### WalletTransaction (2 unexposed fields)
| Field | Type | Impact |
|-------|------|--------|
| `UserId` | string | Expected — injected from auth context |
| `RelatedPurchaseId` | Guid? | Cannot trace transaction to purchase |

### TrackCollection (1 unexposed field)
| Field | Type | Impact |
|-------|------|--------|
| `CreatorId` | string | Owner not returned in TrackCollectionDto |

### Invoice (1 unexposed field)
| Field | Type | Impact |
|-------|------|--------|
| `UserId` | string | Owner not returned in InvoiceResponse |

### LibraryItem (2 unexposed fields)
| Field | Type | Impact |
|-------|------|--------|
| `PurchaseId` | Guid? | Cannot trace library item to purchase |
| `SavedAt` | DateTime | When item was saved not visible |

---

## 9. DTO Fields with No Corresponding Entity Field

These DTO fields do not map to any entity property. Some are legitimately computed; others may be mapping bugs.

### Computed / Derived (acceptable)
| DTO | Field | Type | Source |
|-----|-------|------|--------|
| `TrackResponse` | PlatformFeePercent | decimal | Computed from creator tier |
| `TrackResponse` | NonExclusivePlatformFee | decimal | Computed |
| `TrackResponse` | NonExclusiveCreatorEarnings | decimal | Computed |
| `TrackResponse` | ExclusivePlatformFee | decimal | Computed |
| `TrackResponse` | ExclusiveCreatorEarnings | decimal | Computed |
| `TrackResponse` | CopyrightBuyoutPlatformFee | decimal | Computed |
| `TrackResponse` | CopyrightBuyoutCreatorEarnings | decimal | Computed |
| `UserProfileResponse` | UploadLimit | int? | Computed from tier |
| `UserProfileResponse` | PlatformFeePercent | decimal | Computed from tier |
| `UserProfileResponse` | ContractVersion | string | Hardcoded |
| `BillingStatusResponse` | UploadLimit | int? | Computed from tier |
| `BillingStatusResponse` | PlatformFeePercent | decimal | Computed from tier |
| `CreatorProfileDto` | Stats | CreatorStatsDto | Aggregated from purchases |
| `CreatorStatsDto` | TotalDownloads | int | Aggregated |
| `CreatorStatsDto` | TotalEarnings | decimal | Aggregated |
| `LibraryItemResponse` | Purchased | bool | Computed from purchase existence |
| `LibraryItemResponse` | PurchasedOn | string? | Computed from purchase date |
| `CheckoutConfirmResponse` | AddedToLibrary | bool | Computed from operation result |
| `AdminDashboardSummary` | (all fields) | double | Aggregated |

### Potentially problematic (no clear entity mapping)
| DTO | Field | Type | Issue |
|-----|-------|------|-------|
| `TrackResponse.Artist` | Artist | string? | No `Artist` field on `Track` entity. Presumably mapped from `Creator.DisplayName` but naming is misleading. |
| `PurchaseResponse.TrackTitle` | TrackTitle | string | Not on `Purchase` entity. Mapped via navigation `Purchase.Track.Title`. |
| `PurchaseResponse.Currency` | Currency | string | Not on `Purchase` entity. Hardcoded to "usd". |
| `LibraryItemResponse.Genre` | Genre | string? | Not on `LibraryItem` entity. Mapped via navigation `LibraryItem.Track.Genre`. |
| `WalletResponse.Currency` | Currency | string | Not on any wallet entity. Hardcoded to "usd". |
| `PlanResponse` | (all fields) | — | No `Plan` entity exists. This DTO represents configuration data with no backing entity. |

---

## 10. Missing Validation Annotations

### Request DTOs with NO validation at all
| DTO | Fields | Risk |
|-----|--------|------|
| `PayoutRequest` | Amount (decimal) | No [Required], no [Range] — can submit 0 or negative amounts |
| `WithdrawRequest` | Amount (decimal) | Same issue |
| `BillingCheckoutRequest` | Tier (string?) | No [Required] — can submit null tier |
| `UpsertCreatorProfileRequest` | Slug, Bio, Niche, SocialLinks, ShowEarnings, ShowDownloadStats | No validation at all — Slug could be empty/malicious |
| `UpsertCollectionRequest` | Title, Description, TrackIds | No validation — Title should be [Required] for creation |
| `UpdatePinnedTracksRequest` | TrackIds | No validation |
| `CreditCreatorRequest` | CreatorId, TrackId, TrackTitle, AmountCents, LicenseType | Admin endpoint with zero validation — AmountCents could be negative |
| `PaymentCheckoutRequest` | TrackId, LicenseType, UsageType | TrackId nullable with no validation |

### Request DTOs with partial validation gaps
| DTO | Field | Issue |
|-----|-------|-------|
| `PurchaseCreateRequest` | TrackId | Has default "" but no [Required] annotation |
| `ForgotPasswordRequest` | Email, PhoneNumber | Both nullable, no custom validation requiring at least one |
| `RecoverUsernameRequest` | Email, PhoneNumber | Same issue |
| `VerifyCodeRequest` | Email, PhoneNumber | Same — Code is validated but identifier is not |
| `ResetPasswordRequest` | Email, PhoneNumber | Same — Code and NewPassword validated but identifier is not |
| `UploadTrackRequest` | Price, NonExclusivePrice, ExclusivePrice, CopyrightBuyoutPrice | No [Range] — could be negative |

---

## 11. Naming Inconsistencies

| Issue | Details |
|-------|---------|
| **Cents vs Dollars naming** | Entity: `AmountCents` / `NonExclusivePriceCents` (int, cents). DTO: `Amount` / `NonExclusivePrice` (decimal, dollars). The naming doesn't signal the unit conversion. |
| **Id type inconsistency across DTOs** | `SubscriptionResponse.Id` is `Guid`, but all other response DTOs (`PurchaseResponse`, `InvoiceResponse`, `WalletTransactionResponse`, etc.) use `string` for `Id`. |
| **AuthResponse.UserId** is `Guid` | But `UserProfileResponse.UserId` is `string`. Two DTOs for the same user with different ID types. |
| **LicenseCertificate.TrackId** | This is a `string` containing the CambrianTrackId (e.g. "CAMB-TRK-XXXX"), but `Purchase.TrackId` is a `Guid` FK. Same field name, completely different semantics. |
| **CreatorBalance.CreatorId** is `Guid` | Every other entity uses `string` for user FKs (Identity convention). Orphaned entity but still confusing. |
| **ModerationAction uses DateTimeOffset** | Every other entity uses `DateTime`. Orphaned but inconsistent. |
| **TrackResponse.Id defaults to `Guid.NewGuid().ToString()`** | Every other DTO defaults to `""` or `string.Empty`. This creates a new GUID on DTO construction. |
| **Payout.RequestedAt** vs standard `CreatedAt` | Every other entity uses `CreatedAt` for the creation timestamp. Payout uses `RequestedAt`. |
| **LibraryItem.SavedAt** vs standard `CreatedAt` | Same issue — non-standard timestamp naming. |

---

## 12. Duplicate/Overlapping Definitions

| Issue | Details |
|-------|---------|
| **Two `CheckoutResponse` classes** | `Cambrian.Application.DTOs.Billing.CheckoutResponse` (only `CheckoutUrl`) and `Cambrian.Application.DTOs.Checkout.CheckoutResponse` (`CheckoutUrl` + `Status` + `LicenseCertificate`). Different capabilities, same class name. |
| **`User` entity vs `ApplicationUser` entity** | `User` has `Guid Id`, `UserRole` enum. `ApplicationUser` has `string Id`, `string Role`. Both represent users. Only `ApplicationUser` is registered in DbContext. |
| **`License` entity vs `LicenseCertificate` entity** | `License` uses `LicenseType` enum and has `Price`. `LicenseCertificate` uses string `LicenseType` and has no price. Only `LicenseCertificate` is registered in DbContext. |
| **`ApplicationUser.Tier` (string) vs `ApplicationUser.CreatorTier` (enum)** | Two tier fields on the same entity with different types and different value sets. Confusing overlap. |
| **`ApplicationUser.Plan` (string?) vs `Subscription.Plan` (string)** | Plan stored in two places — on the user and on a separate Subscription entity. |
| **`Purchase.LicenseType` vs `Track.LicenseType`** | Both are `string?` — unclear which is authoritative for a given purchase. |

---

## 13. Structural/Architectural Issues

### 13.1 Navigation Property Gaps

| Entity | Missing Navigation | Impact |
|--------|--------------------|--------|
| `CreatorProfile` | No `ApplicationUser` navigation property | Cannot eagerly load user data with profile |
| `TrackCollection` | No `ApplicationUser` navigation for `CreatorId` | Cannot eagerly load creator |
| `TrackFile` | No `Track` navigation for `TrackId` | Completely disconnected |
| `CreatorBalance` | No `ApplicationUser` navigation for `CreatorId` | Completely disconnected; also uses wrong ID type |
| `Payment` | No `Purchase` navigation for `PurchaseId` | Completely disconnected |
| `StreamSession` | No `ApplicationUser` navigation for `UserId` | Cannot eagerly load user |
| `AnalyticsEvent` | No navigation properties at all | By design (event sourcing), acceptable |
| `AuditLog` | No navigation properties | By design, acceptable |
| `AbuseReport` | No `ApplicationUser` navigation for `ReportedByUserId` | Cannot eagerly load reporter |
| `WalletTransaction` | No `Purchase` navigation for `RelatedPurchaseId` | Cannot trace transaction to purchase |

### 13.2 Denormalization Risks

| Field | Issue |
|-------|-------|
| `ApplicationUser.WalletBalanceCents` | Denormalized balance. Must stay in sync with `WalletTransaction` records. No database-level constraint. |
| `ApplicationUser.UploadCount` | Denormalized count. Must stay in sync with `Track` records for that creator. |
| `LibraryItem.Title`, `Artist`, `AudioUrl` | Copied from Track at save time. If Track is updated, LibraryItem data becomes stale. |
| `StreamSession.Title` | Copied from Track. Same staleness risk. |
| `Track.ExclusiveSold` | Denormalized flag. Must stay in sync with Purchase records for exclusive licenses. |
| `CreatorProfile.PinnedTrackIds` | Comma-separated GUIDs stored as string. No referential integrity. Tracks could be deleted while still pinned. |
| `TrackCollection.TrackIds` | Same CSV storage pattern. Same referential integrity risk. |

### 13.3 PayoutResponse is Dangerously Sparse

`PayoutResponse` contains only `Amount` (decimal) and `Status` (string). It is missing:
- `Id` — caller cannot reference the payout
- `CreatorId` — cannot identify who the payout is for
- `RequestedAt` — cannot show when it was requested
- `CompletedAt` — cannot show when it was fulfilled

This is the most incomplete response DTO in the codebase.

---

## 14. Summary of All Issues

| Category | Count | Severity |
|----------|-------|----------|
| Orphaned entities (no DbSet, no repository) | 6 | Medium — dead code, confusion |
| Enum vs string mismatches (enum exists but string used) | 9 | **High** — PayoutStatus values don't match, LicenseType "non-exclusive" has no enum member |
| Type mismatches (DTO ↔ entity) | 17 | **High** — `AuthResponse.UserId` Guid vs string could cause runtime errors |
| Nullable mismatches | 11 | Medium — silent null→empty conversions |
| Entity fields not in any DTO | 28 | Medium — Mood/Tempo/Tags/Visibility invisible despite filters existing |
| DTO fields with no entity backing | 22 | Low — mostly computed, but `Artist` mapping is unclear |
| Missing validation annotations | 20+ fields across 8 DTOs | **High** — admin endpoints with zero validation, negative amounts possible |
| Naming inconsistencies | 9 | Medium — cents vs dollars confusion, mixed ID types |
| Duplicate/overlapping definitions | 6 | Medium — two CheckoutResponse classes, two user entities |
| Missing navigation properties | 10 | Low-Medium — prevents eager loading |
| Denormalization risks | 7 | Medium — stale data if not carefully managed |

### Top 5 Most Critical Issues

1. **`AuthResponse.UserId` is `Guid` but `ApplicationUser.Id` is `string`** — will throw `FormatException` if Identity generates a non-GUID ID string.

2. **`PayoutStatus` enum values don't match `Payout.Status` string values** — "approved"/"rejected"/"completed" in entity vs "Processing"/"Paid"/"Failed" in enum. Using either interchangeably will produce incorrect behavior.

3. **`LicenseType` enum lacks "non-exclusive"** — the most common license type in the codebase ("non-exclusive") has no enum representation. The enum has "Extended" which is never used as a string.

4. **Missing validation on financial DTOs** — `PayoutRequest`, `WithdrawRequest`, and `CreditCreatorRequest` have no `[Range]` validation, allowing zero or negative monetary amounts.

5. **`PayoutResponse` missing Id, timestamps** — cannot identify, reference, or track payout lifecycle from the API response.
