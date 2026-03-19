# Cambrian - Deployment Guide

## Architecture

| Component | Hosting | Staging | Production |
|-----------|---------|---------|------------|
| Frontend  | **Vercel** | `staging.cambrianmusic.com` (or preview URLs) | `cambrianmusic.com` |
| Backend   | **Render** (Docker) | `cambrian-api-staging` service | `cambrian-api` service |
| Database  | **Render PostgreSQL** | `cambrian-db-staging` | `cambrian-db-prod` |
| Storage   | S3-compatible (R2/S3) | `cambrian-audio-staging` bucket | separate production bucket |

---

## 1) Backend - Render (staging)

`render.yaml` already defines an isolated staging stack (DB + API + staging bucket name).

1. **Create/refresh blueprint**
   - Render Dashboard -> **Blueprints** -> New Blueprint Instance
   - Select this repo and confirm `render.yaml` resources
   - Staging API service is configured to deploy from branch `staging`

2. **Set required secrets in Render dashboard**

   | Variable | Required | Notes |
   |----------|----------|-------|
   | `Jwt__Key` | Yes | Generate strong secret (`openssl rand -base64 48`), min length 32 |
   | `Stripe__SecretKey` | Recommended | Use Stripe **test** key in staging |
   | `Stripe__WebhookSecret` | If webhooks enabled | From Stripe webhook endpoint |
   | `Storage__Provider` | Yes (for persistence) | `r2` or `s3` (leave `local` only for temporary testing) |
   | `Storage__Endpoint` | When `r2`/`s3` | e.g. `https://<account-id>.r2.cloudflarestorage.com` |
   | `Storage__AccessKey` | When `r2`/`s3` | Storage credential |
   | `Storage__SecretKey` | When `r2`/`s3` | Storage credential |
   | `Storage__PublicUrl` | Optional | Public bucket URL for cover art links |
   | `Email__ResendApiKey` | If `Email__Provider=resend` | Required by staging email setup |
   | `Admin__Email` / `Admin__Password` | Recommended | Seeds admin account at startup |

   `DATABASE_URL`, `Storage__Bucket=cambrian-audio-staging`, `Storage__Region=auto`, and `Storage__UsePathStyle=true` are already defined by `render.yaml`.

3. **Stripe webhook setup (staging)**
   - Endpoint: `https://<staging-api-domain>/webhook/stripe`
   - Events: `checkout.session.completed`, `customer.subscription.*`, `invoice.*`, `account.updated`
   - Copy signing secret into `Stripe__WebhookSecret`

4. **Verification checks**
   - `GET /health` should return OK
   - `GET /health/storage` should pass when storage credentials are valid
   - Stream/download endpoints should return valid signed URLs (or redirects) for existing tracks

### Production

Production is defined in `render.yaml` alongside staging. The backend enforces strict startup guards — the app **refuses to start** if any of these are misconfigured:

| Guard | Enforced | What happens |
|-------|----------|--------------|
| `Jwt__Key` ≥ 32 chars | All environments | App crashes with `InvalidOperationException` |
| `DATABASE_URL` | Non-Testing | App crashes with `InvalidOperationException` |
| `Stripe__SecretKey` required | Production | App crashes if missing |
| `Stripe__SecretKey` must be `sk_live_` | Production | App crashes if `sk_test_` key is supplied |
| `Storage__Provider` must be `s3` or `r2` | Production | App crashes if `local` or unset |
| `Email__Provider` must be `smtp` or `resend` | Production | App crashes if `console` or unset |
| `App__FrontendUrl` required | Production | App crashes if missing |
| Auto-migration disabled | Production | Pending migrations are logged but NOT applied — run manually |

**Steps:**
1. Set all required secrets in Render dashboard for the production service
2. Run pending migrations manually before deploying (or use a one-off job)
3. Verify `GET /health` returns OK after deploy
4. Configure Stripe production webhook to `/webhook/stripe` with live signing secret

---

## 2) Frontend - Vercel

Vercel deploys preview builds for non-default branches and production builds for the default branch.

| Environment | Variable | Value |
|-------------|----------|-------|
| Preview | `VITE_BACKEND_API` | staging backend URL |
| Preview | `VITE_AUTH_API` | staging backend URL (or dedicated auth URL if split) |
| Production | `VITE_BACKEND_API` | production backend URL |
| Production | `VITE_AUTH_API` | production backend URL (or dedicated auth URL if split) |

If using preview domains heavily, keep backend CORS configured with:
- `App__CorsOrigins` for explicit domains
- `App__VercelProjectSlug` to allow matching `*.vercel.app` previews for the project

---

## 3) Environment Variable Reference (backend)

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `ASPNETCORE_ENVIRONMENT` | Yes | - | `Staging` or `Production` |
| `DATABASE_URL` | Yes (Render) | - | Auto-linked Render PostgreSQL URL |
| `ConnectionStrings__DefaultConnection` | Yes (non-Render) | - | ADO.NET connection string |
| `Jwt__Key` (or `JWT_KEY`) | Yes | - | JWT signing secret (>= 32 chars) |
| `Jwt__Issuer` | No | `cambrian-api` | JWT issuer claim |
| `Jwt__Audience` | No | `cambrian-client` | JWT audience claim |
| `Stripe__SecretKey` | Optional in non-prod | - | Stripe API key |
| `Stripe__WebhookSecret` | When webhooks enabled | - | Stripe webhook signing secret |
| `Storage__Provider` | No | `local` | `local`, `s3`, or `r2` |
| `Storage__Endpoint` | When `s3`/`r2` | - | S3-compatible endpoint URL |
| `Storage__Bucket` | When `s3`/`r2` | `cambrian-audio` | Bucket name |
| `Storage__AccessKey` | When `s3`/`r2` | - | Storage access key |
| `Storage__SecretKey` | When `s3`/`r2` | - | Storage secret key |
| `Storage__Region` | When `s3`/`r2` | `auto` | For R2, keep `auto` |
| `Storage__UsePathStyle` | When `s3`/`r2` | `true` | Required for R2 path-style requests |
| `Storage__PublicUrl` | Optional | - | Public base URL for cover art |
| `Email__Provider` | No | `console` | `console`, `smtp`, `resend`, `sendgrid` |
| `Email__ResendApiKey` | When provider=`resend` | - | Resend API key |
| `Email__SmtpHost` / `Email__SmtpPort` / `Email__SmtpUser` / `Email__SmtpPass` | When provider=`smtp` | - | SMTP settings |
| `App__FrontendUrl` | Yes | - | Primary frontend origin |
| `App__CorsOrigins` | Yes | - | Comma-separated allowed origins |
| `App__VercelProjectSlug` | Optional | - | Allow Vercel preview origins matching project slug |
| `RateLimiting__GlobalPermitLimit` | No | `100` | Requests/min per IP |
| `RateLimiting__AuthPermitLimit` | No | `10` | Auth requests/min per IP |

---

## 4) Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| Signed URL returns `403` from R2 | Wrong signature region/path style | Set `Storage__Region=auto` and `Storage__UsePathStyle=true`; confirm endpoint/bucket/keys |
| `health/storage` fails | Missing or invalid storage credentials | Recheck `Storage__Provider`, endpoint, bucket, access key, secret key |
| CORS failures from preview frontends | Origin not allowlisted | Add explicit origin to `App__CorsOrigins` or set `App__VercelProjectSlug` |
| Webhook signature verification fails | Wrong webhook secret | Copy latest Stripe endpoint secret into `Stripe__WebhookSecret` |
| Uploads disappear after redeploy | Using local storage in containers | Move to `Storage__Provider=r2` or `s3` with persistent bucket |

---

## 5) Quick Deploy Checklist

### Local Development Setup

`appsettings.Development.json` ships with empty secrets. Use `dotnet user-secrets` to set credentials locally (never commit real values):

```bash
cd src/Cambrian.Api
dotnet user-secrets init   # one-time
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=cambrian;Username=postgres;Password=postgres"
dotnet user-secrets set "Jwt:Key" "my-local-dev-secret-key-min-32-chars!!"
dotnet user-secrets set "Stripe:WebhookSecret" "whsec_..."
```

### Staging
- [ ] Deploy/refresh Render blueprint from `render.yaml`
- [ ] Set required secrets (`Jwt__Key`, Stripe, storage, email, admin seed)
- [ ] Verify `GET /health` and `GET /health/storage`
- [ ] Configure Stripe staging webhook to `/webhook/stripe`
- [ ] Set Vercel preview env (`VITE_BACKEND_API`, `VITE_AUTH_API`)
- [ ] Validate stream/download signed URL behavior on a real track

### Production
- [ ] Set production secrets (live Stripe keys, distinct storage bucket, JWT key)
- [ ] Configure frontend and CORS origins
- [ ] Verify health endpoints, webhook delivery, and storage signed URLs