# Cambrian Database Operations Audit Report

**Date:** 2026-03-13
**Auditor:** Automated Database QA
**Scope:** Track uploads, creator ownership, purchases, licenses, user library access
**Codebase Version:** commit 93ea36f6dc47be1e884dae8bab1b9d541e1feafc

---

## TEST 1: Track Uploads

**STATUS: PASS**

### Evidence

- `UploadController` (POST /upload) sets `CreatorId` from JWT `NameIdentifier` claim before delegating to `UploadService`.
- `UploadService.Upload` validates audio file extension, MIME type, and size (100 MB limit). Cover art is validated separately (10 MB limit).
- A new `Track` entity is created with `Guid.NewGuid()` as ID and `TrackIdDto.Generate()` for the human-readable `CambrianTrackId`.
- `CambrianTrackId` has a unique index in the database (`CambrianDbContext` line 48).
- Track is persisted via `TrackRepository.AddAsync` → `_db.SaveChangesAsync()`.
- File is uploaded to object storage BEFORE the database record is created, ensuring the URL is valid at insert time.

### Risk Level: LOW

### Recommendation

Minor: If `_db.SaveChangesAsync()` fails after the storage upload succeeds, an orphaned file remains in object storage with no database reference. Consider adding a compensation/cleanup mechanism for failed DB inserts.

---

## TEST 2: Creator Ownership

**STATUS: PASS**

### Evidence

- `Track.CreatorId` is set from the authenticated user's JWT claim (`ClaimTypes.NameIdentifier`), not from user-supplied input. See `UploadController` line 27.
- The `[RequireCreatorTier]` middleware enforces that only users with creator-tier access can upload tracks.
- Foreign key relationship: `Track.CreatorId → ApplicationUser.Id` with `OnDelete(DeleteBehavior.Restrict)` — prevents deleting a user who owns tracks.
- `LicenseCertificate.CreatorId` is derived from `Track.CreatorId` at purchase time (not from request input).
- `WalletTransaction.UserId` for creator credits is set from `Track.CreatorId`, ensuring revenue goes to the correct creator.
- Navigation properties: `ApplicationUser.Tracks` collection is properly configured with bidirectional relationship.

### Risk Level: LOW

### Recommendation

None — ownership chain is correctly enforced from upload through purchase and revenue crediting.

---

## TEST 3: Purchases

**STATUS: FAIL**

### Evidence

Three purchase creation paths exist, with inconsistencies across them:

#### Path A — CheckoutService.ConfirmAsync (Primary flow)

- **No database transaction.** Six separate repository calls each invoke `SaveChangesAsync()` independently:
  1. `_purchases.AddAsync(purchase)` — line 185
  2. `_tracks.UpdateAsync(track)` — line 191 (exclusive marking)
  3. `_library.AddAsync(libraryItem)` — line 209 or `_library.UpdateAsync` — line 214
  4. `_wallet.AddTransactionAsync(tx)` — line 236
  5. `_licenseService.IssueCertificateAsync(...)` — line 247 (internally calls `_repo.AddAsync`)
  6. `_purchases.UpdateAsync(purchase)` — line 260 (links LicenseId)

  If any step fails after the purchase is created, the database is left in an inconsistent state (e.g., purchase exists but no library entry, no wallet credit, or no license).

- **No invoice created.** Unlike `PurchaseService.CreateAsync`, this path does not create an `Invoice` record. The `MarketplaceIntegrityService.CheckCompletedPurchasesHaveInvoices` audit rule would flag these as violations.

- **Duplicate purchase race condition.** The duplicate check (lines 147-149) loads all buyer purchases into memory and filters in C#. Between the check and the insert, a concurrent request could create a duplicate. There is no unique compound index on `(BuyerId, TrackId, LicenseType)` to enforce this at the DB level.

#### Path B — StripeWebhookService.HandleTrackPurchase (Webhook flow)

- **Uses database transaction** (`BeginTransactionAsync` in `ProcessEventAsync`). This is correct.
- **Event deduplication** via `StripeWebhookEvents` table with unique index on `EventId`. This is correct.
- **Atomic exclusive marking** via raw SQL `UPDATE ... WHERE ExclusiveSold = false`. This is correct.
- **No invoice created** — same gap as Path A.

#### Path C — PurchaseService.CreateAsync (Legacy /purchases endpoint)

- **No database transaction.** Multiple repository calls are not atomic.
- **LibraryItem created without PurchaseId.** Line 97-106: the `LibraryItem` is created but `PurchaseId` is not set, breaking the linkage that `CheckoutService` and `StripeWebhookService` maintain.
- **CreditCreatorAsync is a no-op.** Line 158-163: the method body is `return Task.CompletedTask`, so creators receive no wallet credit for purchases through this path.
- **Invoice IS created** on this path (lines 109-120). This is the only path that creates invoices.

### Risk Level: HIGH

### Recommendation

1. Wrap all purchase-related operations in `CheckoutService.ConfirmAsync` in a database transaction.
2. Add a composite unique index on `Purchases(BuyerId, TrackId, LicenseType)` to enforce duplicate prevention at the DB level.
3. Create invoice records in all purchase paths (checkout confirmation and webhook).
4. Either implement `PurchaseService.CreditCreatorAsync` or remove the legacy endpoint.
5. Set `PurchaseId` on `LibraryItem` in `PurchaseService.CreateAsync`.

---

## TEST 4: Licenses

**STATUS: FAIL**

### Evidence

- **Duplicate prevention** exists at the application level: `LicenseService.IssueCertificateAsync` checks `_repo.GetByPurchaseIdAsync(purchaseId)` before creating. However, there is **no unique index on `LicenseCertificates.PurchaseId`** in the database schema. Concurrent requests could create duplicate certificates for the same purchase.

- **License issuance failure is silently swallowed.** In both `CheckoutService.ConfirmAsync` (line 264) and `StripeWebhookService.HandleTrackPurchase` (line 385), exceptions from `_licenseService.IssueCertificateAsync` are caught and logged as warnings. The purchase completes successfully without a license certificate. The `Purchase.LicenseId` remains null.

- **No FK from `LicenseCertificate.TrackId` to `Track.Id`.** The `LicenseCertificate.TrackId` field stores the `CambrianTrackId` string (e.g., "CAMB-TRK-A1B2C3D4"), not the `Track.Id` GUID. While `CambrianTrackId` has a unique index on the `Tracks` table, there is no foreign key constraint linking `LicenseCertificate.TrackId` to `Track.CambrianTrackId`. If a track is deleted (or its `CambrianTrackId` changes), the license certificate becomes an orphan with no referential integrity enforcement.

- **`Purchase ↔ LicenseCertificate` relationship is configured as 1:1** (`HasOne...WithOne...HasForeignKey<Purchase>(p => p.LicenseId)`), but the `LicenseCertificate` entity also has its own `PurchaseId` FK. This creates a bidirectional 1:1 where either side could be null, and there is no constraint ensuring both sides agree.

### Risk Level: MEDIUM

### Recommendation

1. Add a unique index on `LicenseCertificates.PurchaseId` to prevent duplicate certificates at the DB level.
2. Consider making license issuance failure a transaction-rolling-back error, or implement a retry/compensation mechanism.
3. Add a foreign key from `LicenseCertificate.TrackId` to `Track.CambrianTrackId` for referential integrity.

---

## TEST 5: User Library Access

**STATUS: FAIL**

### Evidence

- **Download access control is bypassable.** `DownloadController.Download` (line 34) checks only for a `LibraryItem` entry via `_library.GetByUserAndTrackAsync(userId, id)`. It does NOT verify that the library item is backed by a completed purchase. `LibraryService.SaveAsync` allows any authenticated user to add any existing track to their library (POST /library) without purchasing it. This means:

  1. User calls `POST /library` with `{ "trackId": "<any-track-guid>" }` — library entry created (free save).
  2. User calls `GET /download/{trackId}` — download succeeds because library entry exists.
  3. User has downloaded a paid track without paying.

- **Unique constraint is enforced correctly.** The `LibraryItem` table has a unique index on `(UserId, TrackId)` (`CambrianDbContext` line 91), and the application also checks for duplicates before inserting (`LibraryService.SaveAsync` line 57).

- **Library deletion is unrestricted.** `LibraryService.RemoveAsync` allows removing any library item regardless of purchase status. A user could remove a purchased item and lose their access record. The `Purchase` record remains, but the library linkage is broken.

- **Orphaned library items are checked.** `MarketplaceIntegrityService.CheckOrphanedLibraryItems` verifies that all library items reference valid tracks. FK constraint with `OnDelete(DeleteBehavior.Restrict)` on `LibraryItem → Track` also prevents track deletion when library references exist.

- **Streaming has no access control.** `StreamController` does not check for library membership or purchase status — audio streaming is open to all authenticated (or even unauthenticated) users. This may be by design for preview/discovery, but combined with the download bypass, it means tracks have no effective paywall.

### Risk Level: CRITICAL

### Recommendation

1. `DownloadController` must verify that the `LibraryItem` has a non-null `PurchaseId` linked to a completed purchase before allowing download.
2. Consider restricting `LibraryService.SaveAsync` to only allow saving free tracks (price = 0) or tracks the user has already purchased.
3. Consider preventing deletion of library items that are backed by purchases.

---

## Summary

| # | Test | Status | Risk Level |
|---|------|--------|------------|
| 1 | Track Uploads | **PASS** | Low |
| 2 | Creator Ownership | **PASS** | Low |
| 3 | Purchases | **FAIL** | High |
| 4 | Licenses | **FAIL** | Medium |
| 5 | User Library Access | **FAIL** | Critical |

### Critical Findings

1. **Download paywall bypass** (Test 5): Any user can save any track to their library and download it without purchasing. This completely undermines the monetization model.
2. **No transactional consistency** (Test 3): The primary checkout flow (`CheckoutService.ConfirmAsync`) performs 6 independent database writes with no transaction. Partial failures create inconsistent state.
3. **Missing invoice records** (Test 3): The two primary purchase paths (checkout confirmation and webhook) do not create invoice records.
4. **Silent license failures** (Test 4): License certificate creation failures are caught and logged but do not prevent the purchase from completing, leaving purchases without legal licensing documentation.
5. **No-op creator crediting** (Test 3): `PurchaseService.CreditCreatorAsync` has an empty implementation, meaning creators receive no revenue from purchases through the legacy `/purchases` endpoint.
