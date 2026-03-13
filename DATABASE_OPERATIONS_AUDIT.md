# Cambrian Backend API — Database Operations Audit Report

**Date:** 2026-03-13  
**Auditor:** Database QA Engineer (Automated)  
**Scope:** Track uploads, creator ownership, purchases, licenses, user library access  
**Codebase:** ASP.NET Core 8, Entity Framework Core 8, PostgreSQL  
**All 243 existing tests: PASSING**

---

## TEST 1: Track Uploads

**STATUS: PASS**

**EVIDENCE:**
- `UploadService.Upload()` creates a `Track` entity with all required fields: `Id` (new GUID), `CambrianTrackId` (auto-generated via `TrackIdDto.Generate()`), `Title`, `CreatorId`, `AudioUrl`, pricing fields.
- `CambrianTrackId` has a unique index in the database (`OnModelCreating` line 48: `e.HasIndex(t => t.CambrianTrackId).IsUnique()`), preventing duplicate track IDs.
- `CreatorId` is set from the JWT claim in `UploadController` (line 26: `request.CreatorId = User.FindFirstValue(ClaimTypes.NameIdentifier)`), preventing spoofing.
- `TrackRepository.AddAsync()` calls `SaveChangesAsync()` to persist the record.
- File validation enforces allowed extensions, MIME types, and size limits before storage upload.
- Audio file is uploaded to object storage before the DB record is created, ensuring no DB record exists for failed uploads.

**RISK LEVEL:** Low

**RECOMMENDATION:** Consider adding a cleanup mechanism for orphaned storage objects if `TrackRepository.AddAsync()` fails after a successful storage upload.

---

## TEST 2: Creator Ownership

**STATUS: PASS**

**EVIDENCE:**
- `Track.CreatorId` has a foreign key to `ApplicationUser` with `DeleteBehavior.Restrict` (DbContext line 52-55), preventing deletion of users who own tracks.
- `[Authorize]` and `[RequireCreatorTier]` attributes on `UploadController.Upload()` ensure only authenticated creator-tier users can upload.
- `CreatorService.GetTracksAsync()` correctly filters by `creatorId` using `TrackRepository.GetByCreatorIdAsync()`.
- `CreatorService.GetRevenueAsync()` correctly scopes revenue to tracks owned by the requesting creator.
- Track browse queries include `.Include(t => t.Creator)` for proper navigation property loading.
- No API endpoint allows changing `Track.CreatorId` after creation — ownership is immutable.

**RISK LEVEL:** Low

**RECOMMENDATION:** None. Creator ownership model is sound.

---

## TEST 3: Purchases

**STATUS: FAIL**

**EVIDENCE:**

### Finding 3a — Missing Transaction in CheckoutService.ConfirmAsync() (CRITICAL)
`CheckoutService.ConfirmAsync()` (lines 170-280) performs 6 sequential write operations without an explicit database transaction:
1. `_purchases.AddAsync(purchase)` — each calls `SaveChangesAsync()` independently
2. `_tracks.UpdateAsync(track)` — exclusive flag
3. `_library.AddAsync(libraryItem)` or `_library.UpdateAsync(existingLib)`
4. `_wallet.AddTransactionAsync(tx)` — creator wallet credit
5. `_licenseService.IssueCertificateAsync(...)` — license certificate
6. `_purchases.UpdateAsync(purchase)` — link license back

If any step fails mid-flow (e.g., wallet credit fails after purchase + library are saved), the earlier commits are NOT rolled back, leaving the system in a partially fulfilled state: a purchase exists with no wallet credit and no license certificate.

### Finding 3b — Missing Invoice Creation in Primary Purchase Paths
`CheckoutService.ConfirmAsync()` and `StripeWebhookService.HandleTrackPurchase()` do NOT create `Invoice` records. Only the legacy `PurchaseService.CreateAsync()` creates invoices (line 108-120). Purchases completed through the Stripe checkout flow (the primary path) will have no corresponding invoice in the database.

### Finding 3c — PurchaseService Library Duplicate Risk
`PurchaseService.CreateAsync()` (line 97-106) adds a `LibraryItem` without checking for existing entries. If a user saved a track to their library before purchasing, the insert will violate the DB unique index on `(UserId, TrackId)` and throw an exception, aborting the purchase after the `Purchase` record is already committed. This contrasts with `CheckoutService.ConfirmAsync()` which properly checks via `_library.GetByUserAndTrackAsync()`.

### Finding 3d — WalletTransaction.RelatedPurchaseId Has No FK Constraint
`WalletTransaction.RelatedPurchaseId` (nullable Guid) is not configured as a foreign key in `OnModelCreating`. No navigation property exists to `Purchase`. The database cannot enforce that `RelatedPurchaseId` references an actual purchase, allowing dangling references if purchases are deleted.

### Finding 3e — Dual-Path Race Condition (Mitigated)
Both `CheckoutService.ConfirmAsync()` (frontend redirect) and `StripeWebhookService.HandleTrackPurchase()` (webhook) can process the same checkout session. Both have idempotency guards:  the unique index on `StripeSessionId` prevents duplicate purchase records at the DB level. However, the window between check and insert in `ConfirmAsync` (no transaction) could theoretically allow partial duplicates under extreme concurrency.

**RISK LEVEL:** High

**RECOMMENDATION:**
1. Wrap `CheckoutService.ConfirmAsync()` in an explicit `BeginTransactionAsync()` to ensure atomicity of purchase + library + wallet + license creation.
2. Add Invoice creation to `CheckoutService.ConfirmAsync()` and `StripeWebhookService.HandleTrackPurchase()`.
3. Add a `GetByUserAndTrackAsync` check in `PurchaseService.CreateAsync()` before inserting the library item.
4. Add a FK constraint for `WalletTransaction.RelatedPurchaseId` with `SetNull` delete behavior.

---

## TEST 4: Licenses

**STATUS: PASS**

**EVIDENCE:**
- `LicenseService.IssueCertificateAsync()` checks `GetByPurchaseIdAsync(purchaseId)` before creating, preventing duplicate certificates for the same purchase. Verified by test `LicenseService_PreventsDuplicateCertificates`.
- `LicenseCertificate` entity has proper FK relationships: `BuyerId` → `ApplicationUser` (Restrict), `CreatorId` → `ApplicationUser` (Restrict), `PurchaseId` → `Purchase` (Restrict).
- `Purchase.LicenseId` → `LicenseCertificate.Id` is configured as a one-to-one FK with `SetNull` delete behavior.
- License terms (AllowedUses, Restrictions) are correctly derived from `LicenseType` via `ResolveTerms()`.
- Test `LicenseCertificate_Fields_MatchExpectedShape` confirms all required fields are populated.

**Advisory — LicenseType Missing from DTO:**
`LicenseCertificateDto` does not include a `LicenseType` property, and `LicenseService.MapToDto()` does not map it. The license type (standard/non-exclusive/exclusive) is stored in the DB but is not returned to API consumers.

**Advisory — TrackId is Not a Foreign Key:**
`LicenseCertificate.TrackId` stores the human-readable `CambrianTrackId` (string), not the database GUID. There is no FK constraint to the `Tracks` table, so referential integrity is not enforced at the DB level. If a track is deleted, license certificates would have a dangling `TrackId`.

**RISK LEVEL:** Medium (due to advisories)

**RECOMMENDATION:**
1. Add `LicenseType` to `LicenseCertificateDto` and update `MapToDto()`.
2. Consider adding a unique index or FK on `LicenseCertificate.TrackId` referencing `Track.CambrianTrackId`, or store the GUID `TrackId` alongside the human-readable ID.

---

## TEST 5: User Library Access

**STATUS: PASS**

**EVIDENCE:**
- Unique composite index on `(UserId, TrackId)` at DB level (DbContext line 91: `e.HasIndex(l => new { l.UserId, l.TrackId }).IsUnique()`), preventing duplicate library entries at the database level.
- `LibraryService.SaveAsync()` performs an application-level dedup check via `_library.GetByUserAndTrackAsync()` before inserting (line 57-59).
- `CheckoutService.ConfirmAsync()` checks for existing library items before adding (line 195), and upgrades the `PurchaseId` link if the item exists but wasn't linked to a purchase (line 211-215).
- `StripeWebhookService.HandleTrackPurchase()` implements the same idempotent pattern (lines 322-343).
- `DownloadService` correctly gates downloads on `LibraryItem` existence (line 20-22), requiring either a save or a purchase before download access is granted.
- `LibraryItem.PurchaseId` FK is nullable with `SetNull` delete behavior, correctly supporting both saved (free) and purchased items.
- Library items cascade-delete when the user is deleted (`DeleteBehavior.Cascade` on UserId).

**RISK LEVEL:** Low

**RECOMMENDATION:** None. Library access model is well-guarded at both application and database levels.

---

## Cross-Cutting: Missing Records Detection

| Record Type | CheckoutService.ConfirmAsync | StripeWebhookService.HandleTrackPurchase | PurchaseService.CreateAsync |
|---|---|---|---|
| Purchase | Created | Created | Created |
| LibraryItem | Created (idempotent) | Created (idempotent) | Created (NO dedup check) |
| WalletTransaction | Created | Created | NOT created |
| LicenseCertificate | Created | Created | Created |
| Invoice | **NOT created** | **NOT created** | Created |

**Finding:** Invoice records are only created in the legacy `PurchaseService` path. The primary Stripe checkout paths do not generate invoices.

## Cross-Cutting: Duplicate Records Detection

| Entity | DB-Level Protection | Application-Level Protection | Status |
|---|---|---|---|
| Track | Unique index on CambrianTrackId | N/A | PASS |
| Purchase | Unique index on StripeSessionId (filtered) | Check by (BuyerId, TrackId, LicenseType) | PASS |
| LibraryItem | Unique index on (UserId, TrackId) | Check via GetByUserAndTrackAsync (except PurchaseService) | PASS (with advisory on PurchaseService) |
| LicenseCertificate | None | Check by PurchaseId | PASS |
| StripeWebhookEvent | Unique index on EventId | Check via AnyAsync before processing | PASS |

## Cross-Cutting: Broken Relationships

| Relationship | FK Constraint | Status |
|---|---|---|
| Track.CreatorId → ApplicationUser | Yes (Restrict) | PASS |
| Purchase.BuyerId → ApplicationUser | Yes (Restrict) | PASS |
| Purchase.TrackId → Track | Yes (Restrict) | PASS |
| Purchase.LicenseId → LicenseCertificate | Yes (SetNull) | PASS |
| LibraryItem.UserId → ApplicationUser | Yes (Cascade) | PASS |
| LibraryItem.TrackId → Track | Yes (Restrict) | PASS |
| LibraryItem.PurchaseId → Purchase | Yes (SetNull) | PASS |
| LicenseCertificate.BuyerId → ApplicationUser | Yes (Restrict) | PASS |
| LicenseCertificate.CreatorId → ApplicationUser | Yes (Restrict) | PASS |
| LicenseCertificate.PurchaseId → Purchase | Yes (Restrict) | PASS |
| LicenseCertificate.TrackId → Track | **No FK** (string, not GUID) | **FAIL** |
| WalletTransaction.RelatedPurchaseId → Purchase | **No FK** | **FAIL** |

## Cross-Cutting: Failed Transactions

| Operation | Explicit Transaction | Atomicity | Status |
|---|---|---|---|
| Track Upload (UploadService) | No (single SaveChanges) | Atomic | PASS |
| Checkout Confirm (CheckoutService) | **No** (6 independent SaveChanges) | **Not atomic** | **FAIL** |
| Webhook Purchase (StripeWebhookService) | Yes (BeginTransactionAsync) | Atomic | PASS |
| Wallet Withdrawal (WalletRepository) | Yes (Serializable isolation) | Atomic | PASS |
| Legacy Purchase (PurchaseService) | No (4 independent SaveChanges) | **Not atomic** | **FAIL** |

---

## Summary

| Data Flow | Status | Risk Level |
|---|---|---|
| Track Uploads | **PASS** | Low |
| Creator Ownership | **PASS** | Low |
| Purchases | **FAIL** | High |
| Licenses | **PASS** | Medium |
| User Library Access | **PASS** | Low |

**Critical issues requiring remediation:**
1. `CheckoutService.ConfirmAsync()` lacks an explicit transaction — partial writes possible
2. Invoice records are not created in the primary purchase flows
3. `WalletTransaction.RelatedPurchaseId` and `LicenseCertificate.TrackId` lack FK constraints
4. `LicenseType` is omitted from the license certificate API response DTO
