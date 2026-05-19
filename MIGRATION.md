# Render → Fly.io + Supabase migration runbook

Moves the Cambrian production API off Render onto Fly.io, with Postgres on
Supabase (same account already used for object storage).

**Estimated downtime:** ~5 minutes (DNS cutover window only).
**Rollback window:** 48 hours — keep Render running until you're confident.

---

## 0. Prerequisites

- Local: `flyctl` installed (`brew install flyctl` or curl install from fly.io/docs/hands-on/install-flyctl).
- Local: `psql` + `pg_dump` matching Postgres 16 (`brew install postgresql@16`).
- Fly.io account.
- Existing Supabase account (you already have one for storage).
- Render dashboard access for `cambrian-db-prod` connection string.
- Stripe dashboard access (you'll update the webhook URL at cutover).
- DNS provider access for `api.cambrianmusic.com`.

---

## 1. Provision Supabase Postgres

1. Supabase dashboard → **New project**.
   - Name: `cambrian-prod`
   - Region: pick the one closest to Fly's `sea` (US West).
   - Strong DB password — save to your password manager.
   - Postgres version: 16.
2. Once provisioned, go to **Project Settings → Database → Connection string**.
3. Copy the **URI** form (the `postgresql://postgres:...:6543/postgres` pooler URL).
   The app's `DATABASE_URL` handler accepts this format.
4. **Important:** under Connection Pooling, confirm mode is **Transaction**
   (default). Session mode breaks EF Core's prepared statements.

---

## 2 + 3. Snapshot Render → Restore to Supabase → Verify

Get the Render Postgres external connection string from the Render dashboard
→ `cambrian-db-prod` → "External Database URL". Set both URLs in your shell
(do NOT commit them):

```bash
export RENDER_DB_URL="postgresql://cambrian:...@dpg-xxxxx.oregon-postgres.render.com/cambrian_prod"
export SUPABASE_DB_URL="postgresql://postgres:...@db.xxxxx.supabase.co:5432/postgres"
```

Then run the helper script (dumps, restores with confirmation, then diffs
row counts and EF migration history between both DBs):

```bash
./scripts/migrate-render-to-supabase.sh all
```

Or run the phases individually:

```bash
./scripts/migrate-render-to-supabase.sh dump                # writes to ./db-dumps/
./scripts/migrate-render-to-supabase.sh restore <dump-file>
./scripts/verify-db-migration.sh                            # row-count diff
```

The verify script exits non-zero if any table has mismatched row counts or
if the latest `__EFMigrationsHistory` row doesn't match between source and
destination. The expected latest migration is
`20260406220049_AddApiKeysTable` (matches CLAUDE.md §5) — if it matches on
both sides, the app will not re-run migrations on first boot.

---

## 4. Provision Fly app

From repo root (`fly.toml` is already committed):

```bash
fly auth login
fly launch --no-deploy --copy-config --name cambrian-api --region sea
```

When prompted:
- "Would you like to set up a Postgresql database?" → **No** (we use Supabase).
- "Would you like to set up an Upstash Redis database?" → **No**.
- "Create .dockerignore from .gitignore?" → **No** (we already have one).

---

## 5. Set secrets on Fly

`fly secrets set` writes encrypted env vars. They survive deploys.

```bash
fly secrets set \
  DATABASE_URL="$SUPABASE_DB_URL" \
  Jwt__Key="$(openssl rand -base64 48)" \
  Stripe__SecretKey="sk_live_..." \
  Stripe__WebhookSecret="whsec_..." \
  Storage__Provider="s3" \
  Storage__Endpoint="https://xxxxx.supabase.co/storage/v1/s3" \
  Storage__Bucket="cambrian-audio-prod" \
  Storage__AccessKey="..." \
  Storage__SecretKey="..." \
  Storage__PublicUrl="https://xxxxx.supabase.co/storage/v1/object/public" \
  Email__ResendApiKey="re_..." \
  Admin__Email="..." \
  Admin__Password="..." \
  Google__ClientId="..."
```

**IMPORTANT — reuse values from Render.** Pull each secret from the current
Render dashboard so:
- The same `Jwt__Key` keeps existing JWTs valid (don't regenerate unless you
  want to force-logout every user).
- The same `Stripe__WebhookSecret` keeps webhook verification working.
- The same Supabase storage credentials keep audio file URLs valid.

Verify:
```bash
fly secrets list   # shows names + digest, never values
```

---

## 6. First deploy — to *.fly.dev only

Do **not** point DNS yet. Smoke-test on the Fly subdomain first.

```bash
fly deploy
fly logs           # watch for "Now listening on: http://+:8080" + no migration errors
fly open /health   # should return 200
```

Hit a few endpoints against `https://cambrian-api.fly.dev` directly:
- `GET /health` → 200
- `GET /api/v1/tracks?limit=1` → 200 with real data
- `GET /api/v1/genres` → 200

If anything's wrong, fix here. Render is still serving production traffic.

---

## 7. Brief maintenance window — final sync + cutover

If meaningful writes happened during steps 2–6 (purchases, signups), do a
second full sync. Easiest approach: short maintenance window.

```bash
# (Optional) Put Render into maintenance — easiest is to suspend the service
# in Render dashboard, returning 503 to clients for a few minutes.

# Re-dump, restore, and verify — same script, same env vars as step 2-3
./scripts/migrate-render-to-supabase.sh all
```

`./scripts/verify-db-migration.sh` must exit 0 before you proceed to DNS
cutover.

**DNS cutover.** Point `api.cambrianmusic.com` at Fly:

```bash
fly certs create api.cambrianmusic.com
fly certs show api.cambrianmusic.com   # shows the DNS records to add
```

Add the shown `A`/`AAAA` (or `CNAME` to `cambrian-api.fly.dev`) at your DNS
provider. TTL should already be low (≤300s); if not, lower it 24h beforehand.

Wait for cert to issue (1–5 minutes):
```bash
fly certs show api.cambrianmusic.com   # "Status: Ready"
```

---

## 8. Update Stripe webhook URL — CRITICAL

If you skip this step, `checkout.session.completed` events stop arriving,
purchases won't credit creator wallets, and the failure is silent.

1. Stripe dashboard → **Developers → Webhooks**.
2. Edit the existing production webhook endpoint.
3. Change URL from the Render domain to:
   `https://api.cambrianmusic.com/webhook/stripe`
4. Confirm the signing secret (`whsec_...`) — it should already match what
   you set in `Stripe__WebhookSecret` on Fly. If you rotated it, update Fly:
   ```bash
   fly secrets set Stripe__WebhookSecret="whsec_new..."
   ```
5. Send a test event from the Stripe dashboard and verify it appears in
   `StripeWebhookEvents` table on Supabase with `Status = 'completed'`.

---

## 9. Post-cutover verification

- `GET https://api.cambrianmusic.com/health` → 200
- Frontend loads, tracks list renders.
- Log in as a test user → JWT still works (because `Jwt__Key` was reused).
- Do one $0.50 real Stripe purchase end-to-end → verify:
  - `Purchases` row created with `Status = 'completed'`
  - `WalletTransactions` row created with `Type = 'credit'`
  - `LicenseCertificates` row issued
  - `StripeWebhookEvents` row `Status = 'completed'`

---

## 10. Decommission (after 48 hours of stable Fly traffic)

```bash
# In Render dashboard:
#   - Suspend cambrian-api service (keep DB for now)
#   - Wait another 7 days
#   - Delete cambrian-api service
#   - Delete cambrian-db-prod   (only after you have a final pg_dump backup stored elsewhere)
```

Once decommissioned, optionally remove `render.yaml` from the repo:
```bash
git rm render.yaml
git commit -m "Remove Render deployment config after Fly.io migration"
```

(Until then, keeping it lets you rollback by re-creating the Render blueprint.)

---

## Rollback procedure

If Fly turns out broken within the 48h window:

1. DNS: point `api.cambrianmusic.com` back at the Render service.
2. Stripe: switch webhook URL back to the Render domain.
3. **Data direction matters:** any writes that happened on Supabase during
   the Fly window need to flow back to Render before you re-enable it, or
   you lose those rows. Do a `pg_dump` from Supabase → restore to Render
   *before* flipping DNS back.

This is why minimizing the cutover window matters — the longer Fly is live,
the harder a rollback gets.

---

## Cost notes

- **Fly free allowance** covers ~3 shared-cpu-1x 256mb VMs. The 512mb size in
  `fly.toml` is over the strict free RAM allowance and will likely bill at
  the per-GB-second rate — historically a few dollars/month for a low-traffic
  scale-to-zero app. Verify current pricing at fly.io/docs/about/pricing.
- **Supabase free tier**: 500 MB DB storage, 60 direct / 200 pooled
  connections. Project pauses after 7 days of zero activity — fine if the
  app gets any traffic, otherwise log in to the dashboard occasionally to
  keep it active.
- Your existing Supabase storage bucket counts against the same free tier
  storage allowance (1 GB by default).
