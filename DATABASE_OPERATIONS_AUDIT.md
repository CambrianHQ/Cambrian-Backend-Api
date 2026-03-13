# Database Operations Audit Report — Cambrian Backend API

**Auditor:** Database QA Engineer (Automated)  
**Date:** 2026-03-13  
**Scope:** Track uploads, creator ownership, purchases, licenses, user library access  
**Commit Under Audit:** `39a02d2b6d4455953f05a5a6ca9362eddc34f290`

---

## 1. TRACK UPLOADS

**TEST:** Track upload creates a complete, well-formed Track record with correct creator ownership  
**STATUS:** PASS  
**EVIDENCE:**
- `UploadService.Upload()` creates a `Track` entity with all required fields: `Id` (new GUID), `CambrianTrackId` (generated), `Title`, `Genre`, `Price`, `LicenseType`, `AudioUrl`, `CoverArtUrl`, `CreatorId`, `Tags`, pricing in cents
- `CreatorId` is validated (`ArgumentException` thrown if blank) and set from the authenticated request
- `TrackRepository.AddAsync()` calls `_db.Tracks.Add(track)` + `SaveChangesAsync()` — a single atomic write
- DbContext enforces: `Title` required (max 200), `CambrianTrackId` required (max 25) with unique index
- FK constraint `Track.CreatorId → ApplicationUser.Id` with `DeleteBehavior.Restrict` prevents orphaned tracks
- File validation (extension, MIME type, size) occurs before any DB write

**RISK LEVEL:** Low  
**RECOMMENDATION:** None — clean single-record creation with proper validation and FK constraints.

---

## 2. CREATOR OWNERSHIP

**TEST:** Creator-to-track ownership relationship is correctly established and enforced  
**STATUS:** PASS  
**EVIDENCE:**
- `Track.CreatorId` is a required FK to `ApplicationUser` with `DeleteBehavior.Restrict` — a creator cannot be deleted while they own tracks
- `ApplicationUser.Tracks` navigation collection provides bidirectional relationship
- `UploadController` is protected by `[RequireCreatorTier]` filter which verifies the user's tier claim
- `CreatorService` queries tracks via `ITrackRepository.GetByCreatorIdAsync(userId)` — scoped to the authenticated creator
- Revenue calculations in `PayoutService.GetEarningsAsync()` correctly join creator → tracks → purchases

**RISK LEVEL:** Low  
**RECOMMENDATION:** None — ownership model is sound with proper FK constraints and access control.

---

## 3. PURCHASES

**TEST:** Purchase flow creates correct, complete records (Purchase, LibraryItem, Invoice, LicenseCertificate, WalletTransaction)  
**STATUS:** FAIL  
**EVIDENCE:**

### 3a. PurchaseService.CreateAsync — Missing Transaction Wrapper
- Creates **4 separate records** across 4 repository calls, each with its own `SaveChangesAsync()`:
  1. `PurchaseRepository.AddAsync(purchase)`
  2. `LibraryRepository.AddAsync(libraryItem)`
  3. `InvoiceRepository.AddAsync(invoice)`
  4. `LicenseService.IssueCertificateAsync()` → `LicenseCertificateRepository.AddAsync(cert)`
- **No database transaction wraps these operations.** If any step fails after the first `SaveChangesAsync()`, the database is left in an inconsistent state (e.g., purchase exists but no library entry, no invoice, no license).
- Each repository calls `SaveChangesAsync()` independently — these are not batched into a single unit of work.

### 3b. PurchaseService.CreateAsync — Missing PurchaseId on LibraryItem
- The `LibraryItem` created at line 97-106 does **not** set `PurchaseId`, leaving the FK null. This breaks the `Purchase → LibraryItem` linkage.
- In contrast, `CheckoutService.ConfirmAsync()` (line 200) and `StripeWebhookService.HandleTrackPurchase()` (line 329) correctly set `PurchaseId = purchase.Id`.
- The `MarketplaceIntegrityService` does not detect this because it checks by `(UserId, TrackId)` pair, not by `PurchaseId` FK.

### 3c. CheckoutService.ConfirmAsync — Missing Invoice Creation
- Creates Purchase, LibraryItem, WalletTransaction, and LicenseCertificate — but **no Invoice** record.
- `PurchaseService.CreateAsync()` creates invoices. `StripeWebhookService.HandleTrackPurchase()` does not create invoices either.
- The `MarketplaceIntegrityService.CheckCompletedPurchasesHaveInvoices()` audit would flag these as violations.

### 3d. CheckoutService.ConfirmAsync — No Transaction Wrapper
- Performs 5+ sequential repository writes without a transaction:
  1. `_purchases.AddAsync(purchase)` (SaveChanges)
  2. `_tracks.UpdateAsync(track)` (SaveChanges) — for exclusive
  3. `_library.AddAsync(libraryItem)` (SaveChanges)
  4. `_wallet.AddTransactionAsync(tx)` (SaveChanges)
  5. `_licenseService.IssueCertificateAsync()` → `AddAsync(cert)` (SaveChanges)
  6. `_purchases.UpdateAsync(purchase)` (SaveChanges) — to link license
- Partial failure leaves orphaned/inconsistent records.

### 3e. StripeWebhookService.HandleTrackPurchase — Properly Transactional
- Uses `_db.Database.BeginTransactionAsync()` with commit/rollback
- Batches Purchase, LibraryItem, WalletTransaction, LicenseCertificate into single `SaveChangesAsync()` + `CommitAsync()`
- Idempotency via `StripeWebhookEvent` deduplication by `EventId`
- Duplicate purchase check present

### 3f. Duplicate Purchase Detection
- `PurchaseService.CreateAsync()`: checks by `(BuyerId, TrackId)` — any license type blocks re-purchase
- `CheckoutService.ConfirmAsync()`: checks by `(BuyerId, TrackId, LicenseType)` — allows same track with different license
- `StripeWebhookService.HandleTrackPurchase()`: checks by `(BuyerId, TrackId, LicenseType)` — matches checkout
- **Inconsistency:** `PurchaseService` is stricter than the other two paths. A user who bought a non-exclusive license cannot buy an exclusive license via the direct purchase endpoint but can via checkout.

### 3g. Exclusive Race Condition Handling
- `PurchaseService.CreateAsync()`: uses `TryMarkExclusiveSoldAsync()` (atomic SQL `UPDATE ... WHERE ExclusiveSold = false`) — correct
- `StripeWebhookService.HandleTrackPurchase()`: uses same atomic SQL pattern — correct
- `CheckoutService.ConfirmAsync()`: uses **non-atomic** `track.ExclusiveSold = true; await _tracks.UpdateAsync(track)` — race condition possible between read and write

**RISK LEVEL:** HIGH  
**RECOMMENDATION:**
1. Wrap `PurchaseService.CreateAsync()` and `CheckoutService.ConfirmAsync()` in explicit database transactions
2. Set `PurchaseId` on `LibraryItem` in `PurchaseService.CreateAsync()`
3. Add Invoice creation to `CheckoutService.ConfirmAsync()` and `StripeWebhookService.HandleTrackPurchase()`
4. Harmonize duplicate purchase detection logic across all three purchase paths
5. Use atomic `TryMarkExclusiveSoldAsync()` in `CheckoutService.ConfirmAsync()` instead of read-modify-write

---

## 4. LICENSES

**TEST:** License certificates are correctly created and linked to purchases  
**STATUS:** PASS (with caveat)  
**EVIDENCE:**
- `LicenseService.IssueCertificateAsync()` creates a `LicenseCertificate` with all required fields: `TrackId` (CambrianTrackId), `BuyerId`, `CreatorId`, `PurchaseId`, `LicenseType`, `UsageType`, `AllowedUses`, `Restrictions`
- Duplicate prevention: checks `GetByPurchaseIdAsync(purchaseId)` before creating — returns existing if found
- FK constraints: `BuyerId → ApplicationUser`, `CreatorId → ApplicationUser`, `PurchaseId → Purchase` — all with `DeleteBehavior.Restrict`
- License terms are correctly resolved by type (standard, non-exclusive, exclusive)
- `CheckoutService.ConfirmAsync()` and `StripeWebhookService.HandleTrackPurchase()` both link `Purchase.LicenseId` back to the certificate

**Caveat:** License certificate creation failures are caught and logged but do not roll back the purchase:
- `CheckoutService.ConfirmAsync()` line 264: `catch (Exception ex) { _logger.LogWarning(...) }` — purchase completes without license
- `StripeWebhookService.HandleTrackPurchase()` line 385: same pattern — purchase and library added but license may be missing
- This means completed purchases can exist without a license certificate, which is a data integrity gap.

**RISK LEVEL:** Medium  
**RECOMMENDATION:** Consider failing the entire transaction if license issuance fails, or implement a background retry mechanism for failed license issuances.

---

## 5. USER LIBRARY ACCESS

**TEST:** Library items are correctly created and linked when tracks are purchased or saved  
**STATUS:** PASS  
**EVIDENCE:**
- **Unique constraint** on `(UserId, TrackId)` in DbContext prevents duplicate library entries at the database level
- **Application-level dedup:** `LibraryService.SaveAsync()` checks `GetByUserAndTrackAsync()` before adding — prevents duplicate
- **Purchase flows:** All three purchase paths (PurchaseService, CheckoutService, StripeWebhookService) create library entries
- `CheckoutService.ConfirmAsync()` and `StripeWebhookService.HandleTrackPurchase()` both check for existing library items and update `PurchaseId` if the item exists but FK is null — idempotent behavior
- `LibraryItem.UserId → ApplicationUser` with `DeleteBehavior.Cascade` — user deletion cleans up library
- `LibraryItem.TrackId → Track` with `DeleteBehavior.Restrict` — tracks with library references cannot be deleted
- `LibraryItem.PurchaseId → Purchase` with `DeleteBehavior.SetNull` — purchase deletion preserves library access (graceful degradation)

**RISK LEVEL:** Low  
**RECOMMENDATION:** None for the library access model itself. The `PurchaseId` gap noted in test 3b (PurchaseService path) should be addressed.

---

## 6. MISSING RECORDS DETECTION

**TEST:** System has mechanisms to detect missing records  
**STATUS:** PASS  
**EVIDENCE:**
- `MarketplaceIntegrityService.RunAuditAsync()` implements 6 integrity checks:
  1. `CheckCompletedPurchasesHaveLibraryEntries` — detects purchases without library items
  2. `CheckExclusiveSoldTracksNotBrowsable` — detects exclusive tracks still visible
  3. `CheckPayoutAmountsMatchRevenue` — detects overpayment
  4. `CheckOrphanedLibraryItems` — detects library items pointing to deleted tracks
  5. `CheckCompletedPurchasesHaveInvoices` — detects purchases without invoices
  6. `CheckExclusivePurchasesHaveTrackFlag` — detects exclusive purchases where track flag is wrong

**RISK LEVEL:** Low  
**RECOMMENDATION:** Add an integrity check for purchases without license certificates.

---

## 7. DUPLICATE RECORDS DETECTION

**TEST:** System prevents duplicate records across all data flows  
**STATUS:** PASS (with caveats)  
**EVIDENCE:**
- **LibraryItem:** Unique index `(UserId, TrackId)` + application-level check — protected
- **Purchase:** Application-level duplicate check in all three paths — protected (but logic varies, see 3f)
- **LicenseCertificate:** Duplicate check by `PurchaseId` — protected
- **StripeWebhookEvent:** Unique index on `EventId` + application-level idempotency check — protected
- **Track.CambrianTrackId:** Unique index — protected
- **Purchase.StripeSessionId:** Unique filtered index (non-null only) — protected

**RISK LEVEL:** Low  
**RECOMMENDATION:** The varying duplicate detection logic across purchase paths (noted in 3f) should be harmonized.

---

## 8. BROKEN RELATIONSHIPS DETECTION

**TEST:** Foreign key constraints prevent broken relationships  
**STATUS:** PASS  
**EVIDENCE:**
- All critical FKs have `DeleteBehavior.Restrict`:
  - `Track.CreatorId → ApplicationUser` (Restrict)
  - `Purchase.BuyerId → ApplicationUser` (Restrict)
  - `Purchase.TrackId → Track` (Restrict)
  - `LibraryItem.TrackId → Track` (Restrict)
  - `LicenseCertificate.BuyerId, CreatorId, PurchaseId` (all Restrict)
  - `Payout.CreatorId → ApplicationUser` (Restrict)
  - `Invoice.PurchaseId → Purchase` (Restrict)
- Cascade deletes are limited to:
  - `User → LibraryItems` (Cascade) — appropriate
  - `User → Subscriptions, Invoices, WalletTransactions` (Cascade) — appropriate
  - `Track → AbuseReports, StreamSessions` (Cascade) — appropriate
- `MarketplaceIntegrityService.CheckOrphanedLibraryItems()` provides runtime orphan detection

**RISK LEVEL:** Low  
**RECOMMENDATION:** None — FK constraints are well-designed with appropriate delete behaviors.

---

## 9. FAILED TRANSACTIONS

**TEST:** Failed multi-step operations are handled with proper rollback  
**STATUS:** FAIL  
**EVIDENCE:**

### 9a. StripeWebhookService — PASS
- `ProcessEventAsync()` uses `BeginTransactionAsync()` / `CommitAsync()` / `RollbackAsync()` with `finally { DisposeAsync() }`
- All writes within the transaction scope are properly guarded

### 9b. WalletRepository.AtomicWithdrawAsync — PASS
- Uses `IsolationLevel.Serializable` transaction to prevent double-withdrawal race conditions
- Balance check and debit are atomic; rollback on exception

### 9c. PurchaseService.CreateAsync — FAIL
- 4 sequential `SaveChangesAsync()` calls with no transaction wrapper
- Failure after step 1 leaves a Purchase record with no LibraryItem, Invoice, or License
- No compensating logic to clean up on partial failure

### 9d. CheckoutService.ConfirmAsync — FAIL
- 6 sequential `SaveChangesAsync()` calls with no transaction wrapper
- Failure mid-flow leaves inconsistent state
- LicenseCertificate failure is swallowed (logged but not rolled back)

### 9e. PayoutService.RequestAsync — PARTIAL PASS
- Creates Payout + WalletTransaction (debit) without a transaction
- If Stripe transfer fails, compensating WalletTransaction (credit) is added — eventual consistency approach
- However, if the compensating credit write fails, the wallet balance is permanently wrong
- Not using `WalletRepository.AtomicWithdrawAsync()` which exists and provides proper transaction isolation

**RISK LEVEL:** HIGH  
**RECOMMENDATION:**
1. Wrap `PurchaseService.CreateAsync()` in a transaction
2. Wrap `CheckoutService.ConfirmAsync()` in a transaction
3. Use `AtomicWithdrawAsync()` in `PayoutService.RequestAsync()` instead of manual debit+compensate

---

## SUMMARY TABLE

| # | Test | Status | Risk |
|---|------|--------|------|
| 1 | Track Uploads | **PASS** | Low |
| 2 | Creator Ownership | **PASS** | Low |
| 3 | Purchases | **FAIL** | HIGH |
| 4 | Licenses | **PASS** (caveat) | Medium |
| 5 | User Library Access | **PASS** | Low |
| 6 | Missing Records Detection | **PASS** | Low |
| 7 | Duplicate Records Detection | **PASS** (caveat) | Low |
| 8 | Broken Relationships | **PASS** | Low |
| 9 | Failed Transactions | **FAIL** | HIGH |

**Overall Assessment:** 7 PASS, 2 FAIL

The two FAIL findings both stem from the same root cause: **multi-record database operations in `PurchaseService` and `CheckoutService` lack transaction wrappers**, creating risk of partial writes and inconsistent state. The `StripeWebhookService` path (the primary production flow via Stripe webhooks) is correctly transactional, which mitigates the severity somewhat since most real purchases flow through the webhook path. However, the `CheckoutService.ConfirmAsync()` polling path and the direct `PurchaseService.CreateAsync()` endpoint remain vulnerable.
