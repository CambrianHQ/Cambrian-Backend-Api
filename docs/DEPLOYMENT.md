# Cambrian Deployment (Railway)

**Platform:** Railway for both services. Replaces `render.yaml` (Render) and Vercel (frontend).
**Last updated:** 2026-06-06. This file is the env source of truth once `render.yaml` is deleted.

> Migrations auto-apply on startup (`app.RunMigrationsAsync()`, skipped in `Testing`). `dotnet run -- --seed` runs migrations + seed then exits (CI/manual setup).

---

## Services

| Service | Repo | Build | Start | Health |
|---|---|---|---|---|
| `cambrian-api` | `Cambrian-Backend-Api` | Docker (`./Dockerfile`, multi-stage, non-root uid 1001, ffmpeg installed for Release Ready) | container entrypoint binds Railway `PORT` | public `GET /health` liveness + admin-authenticated `GET /qa-preflight` |
| `cambrian-web` | `cambrian` | Next.js 15 (Node 22) | `npm run build` → `npm run start:next` | `/` |
| Postgres | Railway plugin | — | — | — |

Postgres: provision a Railway Postgres plugin; it injects `DATABASE_URL`. Keep a persistent volume; one instance per env (the Release Ready worker is in-process).

---

## Backend env vars (`cambrian-api`)

Secrets (set in Railway, never commit). `Section__Key` = .NET config binding.

| Var | Required | Notes |
|---|---|---|
| `DATABASE_URL` | ✅ (auto) | From the Railway Postgres plugin. ADO.NET or `postgres://` URI both accepted. |
| `ASPNETCORE_ENVIRONMENT` | ✅ | `Production` / `Staging`. |
| `PORT` | ✅ (auto) | Railway injects; Dockerfile entrypoint uses it. |
| `Jwt__Key` | ✅ | ≥32 chars. Startup throws if missing/short. |
| `Jwt__Issuer` / `Jwt__Audience` | ✅ | `cambrian-api` / `cambrian-client`. |
| `Stripe__SecretKey` | ✅ prod | Must be `sk_live_` in Production (startup throws on `sk_test_`). |
| `Stripe__WebhookSecret` | ✅ prod | `whsec_` from the **live** webhook. Startup throws if missing in prod. |
| `Stripe__ConnectWebhookSecret` | ✅ prod | `whsec_` from the live `/webhook/stripe/connect` endpoint. Startup throws if missing in prod. |
| `Stripe__Prices__Creator` | ✅ prod | **price_… ID. Startup throws if a Stripe key is set but this is blank.** (was render.yaml-only) |
| `Stripe__Prices__Pro` | ✅ prod | price_… ID. Same boot guard. |
| `Storage__Provider` | ✅ prod | `s3` (Supabase S3 gateway). App refuses to boot with `local` in Production. |
| `Storage__Endpoint` / `Storage__Bucket` / `Storage__AccessKey` / `Storage__SecretKey` / `Storage__PublicUrl` | ✅ | Supabase S3 credentials + bucket. |
| `Storage__Region` | ✅ | **Use a real region (`us-east-1`), NOT `auto`** — Supabase SigV4 rejects `auto` (an R2 leftover). Fix the `"auto"` defaults still in `appsettings*.json`. |
| `Storage__UsePathStyle` | ✅ | `true` for the Supabase gateway. |
| `Email__Provider` | ✅ | `resend` (Production throws on `console`). |
| `Email__ResendApiKey` | ✅ prod | Resend API key; verify the `cambrianmusic.com` domain. |
| `Email__FromAddress` / `Email__FromName` | ✅ | `noreply@cambrianmusic.com` / `Cambrian Music`. |
| `Admin__Email` / `Admin__Password` | ✅ | Seeded admin. Use a strong password (not the `.env` placeholder). |
| `Google__ClientId` | ✅ | Google OAuth. (was render.yaml-only) |
| `Sentry__Dsn` | ⬜ | Error monitoring. No-op if blank. (added; not in old examples) |
| `Provenance__Signing__PrivateKeyPem` | ✅ prod | **EC P-256 PKCS#8 PEM. Startup throws in Production if unset** — provenance stamps must verify across restarts. (new on `feat/entitlements`) |
| `Mastering__Engine` | ⬜ | `ffmpeg` (default) or `tonn`. Leave `ffmpeg` until the RoEx key is provisioned. |
| `Mastering__Tonn__ApiKey` | ⬜ | RoEx key; only needed when `Mastering__Engine=tonn`. |
| `RateLimiting__GlobalPermitLimit` / `RateLimiting__AuthPermitLimit` | ⬜ | Prod `100` / `10`. |
| `ForwardedHeaders__KnownProxies` / `ForwardedHeaders__KnownNetworks` | ⬜ | Comma-separated trusted proxy IPs/CIDRs. Leave unset rather than trusting arbitrary forwarded headers. |
| `App__FrontendUrl` | ✅ | `https://cambrianmusic.com`. |
| `App__CorsOrigins` | ✅ | `https://cambrianmusic.com,https://www.cambrianmusic.com`. |
| `SeedDemoUsers__Password` | ⬜ | Staging/dev only; prod skips demo seeding. |

**Stripe webhooks:** point the platform endpoint at `https://cambrian-backend-api.onrender.com/webhook/stripe`; its signing secret must equal `Stripe__WebhookSecret`. Point the Connect endpoint at `https://cambrian-backend-api.onrender.com/webhook/stripe/connect`; its signing secret must equal `Stripe__ConnectWebhookSecret`.

**Removed vars (do NOT carry over):** `App__VercelProjectSlug`, `App__CloudflarePagesSlug` (off Vercel/CF Pages), `Provenance__Anchor__*` (EVM anchoring deleted — see PHASE 3 cleanup), any `R2_*` (storage is Supabase S3).

---

## Frontend env vars (`cambrian-web`)

`NEXT_PUBLIC_*` are build-time public; `RESEND_*` are server-only.

| Var | Required | Notes |
|---|---|---|
| `NEXT_PUBLIC_API_BASE_URL` | ✅ | `https://cambrian-backend-api.onrender.com`. |
| `NEXT_PUBLIC_GOOGLE_CLIENT_ID` | ✅ | Google OAuth. |
| `NEXT_PUBLIC_SENTRY_DSN` | ⬜ | Frontend Sentry. |
| `NEXT_PUBLIC_TURNSTILE_SITE_KEY` | ✅ | Cloudflare Turnstile (login CAPTCHA — unrelated to R2). |
| `NEXT_PUBLIC_GA_MEASUREMENT_ID` | ⬜ | GA4. |
| `NEXT_PUBLIC_SITE_URL` | ⬜ | Canonical URL for SEO/OAuth. **Add to `.env.example` (currently a gap).** |
| `RESEND_API_KEY` / `RESEND_AUDIENCE_ID` | ✅ | Newsletter/waitlist capture (server-only). Create the Resend audience first. |
| Feature flags (`NEXT_PUBLIC_ENABLE_*`, `NEXT_PUBLIC_MARKETPLACE_LICENSING_COMING_SOON`) | ⬜ | See CLEANUP_INVENTORY.md for current launch states (payouts/community/promotions/checkout dark by default). |

**Removed (off Vercel):** `vercel.json` (deleted), `VERCEL_ENV` / `NEXT_PUBLIC_VERCEL_ENV` and `vercel.live` CSP entries in `next.config.ts` should be stripped. CSP now lives solely in `next.config.ts` `headers()`.

---

## `.env.example` parity gaps to fix (from the audit)
Backend examples are missing: `Stripe__Prices__*`, `Google__ClientId`, `Sentry__Dsn`, `Provenance__Signing__PrivateKeyPem`, `Mastering__*`, `GrowthFeatures__*`, `SeedDemoUsers__Password`. Frontend `.env.example` is missing: `NEXT_PUBLIC_SITE_URL`, `NEXT_PUBLIC_GOOGLE_ALLOWED_HOSTS/ORIGINS`, `NEXT_PUBLIC_ENABLE_COMMUNITY`, `NEXT_PUBLIC_ENABLE_PROMOTIONS`. Reconcile before deleting `render.yaml` (its env list is the most complete current reference).
