# earnings_transactions — append-only artist earnings ledger

> **Write-side owner:** release-pipeline/money-in agent (Connect webhooks).
> **Read-side owner:** earnings read/aggregation agent (`GET /api/me/earnings` etc.).
> Schema changes go through a migration owned by the write side — coordinate here first.

Table: `EarningsTransactions` (EF entity `Cambrian.Domain.Entities.EarningsTransaction`,
migration `20260610182648_AddReleasePipelineAuthorshipEarnings`).

| Column       | Type          | Notes |
|--------------|---------------|-------|
| Id           | uuid PK       | |
| ArtistUserId | varchar(450)  | Artist receiving the earnings (AspNetUsers.Id) |
| Source       | varchar(20)   | `tip` \| `sub` \| `commission` |
| GrossCents   | bigint        | Amount the fan paid |
| FeeCents     | bigint        | Platform fee withheld (`0` for tips at launch; subs: `gross − floor(gross × 0.85)`) |
| NetCents     | bigint        | `GrossCents − FeeCents` — always floored in the artist's favor never rounded up |
| Currency     | varchar(8)    | lowercase ISO, `usd` |
| ExternalRef  | varchar(255)  | Stripe checkout session id (tips, first sub period), invoice id (sub renewals), purchase id (commission) |
| PayerUserId  | varchar(450)? | Paying user when known; null for anonymous |
| CreatedAt    | timestamp     | |

Indexes:
- `IX_EarningsTransactions_Artist_CreatedAt` (ArtistUserId, CreatedAt) — time-series reads
- `IX_EarningsTransactions_Artist_Source` (ArtistUserId, Source) — by-source aggregation
- `IX_EarningsTransactions_Source_ExternalRef` **UNIQUE** — idempotency: one row per
  money event; webhook retries are no-ops

Rules:
1. **Append-only.** Rows are never updated or deleted; corrections are new rows.
2. Writers: `StripeConnectWebhookService` (tips: `checkout.session.completed`;
   subs: first period from `checkout.session.completed`, renewals from
   `invoice.paid` with `billing_reason=subscription_cycle` — `subscription_create`
   invoices are intentionally skipped to avoid double-counting the first period).
3. `commission` is reserved for marketplace sale credits; no writer is wired yet
   (track-sale crediting still flows through the protected `WalletTransactions`
   path). Read-side should treat the source as valid but possibly empty.
4. Balance for `GET /api/me/earnings` = `SUM(NetCents)` per artist (minus payouts,
   which remain in the existing payout tables).
