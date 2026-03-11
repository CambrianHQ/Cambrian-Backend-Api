# Cambrian - Deployment Guide

## Architecture

| Component | Hosting | Staging URL | Production URL |
|-----------|---------|-------------|----------------|
| Frontend  | **Vercel** | cambrian-test.vercel.app | cambrianmusic.com |
| Backend   | **Render** (Docker) | cambrian-api-staging.onrender.com | cambrian-api.onrender.com |
| Database  | **Render** PostgreSQL | cambrian-db-staging | cambrian-db-prod |

---

## 1. Backend - Render

### Staging (ready now)

1. **Create the Blueprint** - push this repo to main, open **Render Dashboard -> Blueprints -> New Blueprint Instance**, select this repo.  Render reads render.yaml and creates:
   - PostgreSQL database cambrian-db-staging
   - Web service cambrian-api-staging (Docker, free plan)

2. **Set secret env vars** in the Render dashboard for cambrian-api-staging:

   | Variable | Value | Notes |
   |----------|-------|-------|
   | Jwt__Key | openssl rand -base64 48 | >= 32 chars, generate a strong random value |
   | Stripe__SecretKey | sk_test_51Sw962Pl... | Your Stripe **test** secret key |
   | Stripe__WebhookSecret | whsec_... | From stripe listen --forward-to (staging) |

   > DATABASE_URL is auto-populated by fromDatabase in render.yaml.

3. **Stripe webhook** - in the Stripe Dashboard -> Webhooks, add an endpoint:
   - URL: https://cambrian-api-staging.onrender.com/webhook/stripe
   - Events: checkout.session.completed, customer.subscription.*, invoice.*, account.updated
   - Copy the signing secret -> paste as Stripe__WebhookSecret

4. **Verify** - once deployed, hit https://cambrian-api-staging.onrender.com/health and confirm status: ok.

### Production (when ready)

1. Uncomment the production service and database blocks in render.yaml
2. Push to main - Render will create the new resources
3. Set env vars in the Render dashboard (same as staging but with **live** Stripe keys and a different JWT secret)
4. Point cambrian-api.onrender.com or add a custom domain in Render settings

---

## 2. Frontend - Vercel

### Staging (Vercel Preview)

Vercel automatically deploys **Preview** builds for every push to non-production branches, and **Production** builds for the default branch.

1. **Link the repo** - in Vercel Dashboard, import the cambrian repo, framework = Vite, output dir = dist.

2. **Set environment variables** in Vercel -> Project Settings -> Environment Variables:

   **Preview** environment:

   | Variable | Value |
   |----------|-------|
   | VITE_BACKEND_API | https://cambrian-api-staging.onrender.com |
   | VITE_AUTH_API | https://cambrian-api-staging.onrender.com |

   **Production** environment:

   | Variable | Value |
   |----------|-------|
   | VITE_BACKEND_API | https://cambrian-api.onrender.com |
   | VITE_AUTH_API | https://cambrian-api.onrender.com |

3. **Custom domain** - in Vercel -> Project Settings -> Domains:
   - Add cambrianmusic.com and www.cambrianmusic.com
   - The vercel.json already redirects www -> apex domain

4. **Verify** - visit https://cambrian-test.vercel.app and confirm the app loads and connects to the staging API.

---

## 3. Environment Variable Reference

### Backend (Render)

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| ASPNETCORE_ENVIRONMENT | Yes | - | Staging or Production |
| DATABASE_URL | Yes | - | Auto-set by Render from linked DB |
| Jwt__Key | Yes | - | JWT signing secret (>= 32 chars) |
| Jwt__Issuer | No | cambrian-api | JWT issuer claim |
| Jwt__Audience | No | cambrian-client | JWT audience claim |
| Stripe__SecretKey | Staging: optional | - | Stripe API secret key |
| Stripe__WebhookSecret | When using webhooks | - | Stripe webhook signing secret |
| Storage__Provider | No | local | local, s3, or r2 |
| Storage__Endpoint | When S3/R2 | - | S3-compatible endpoint URL |
| Storage__Bucket | When S3/R2 | cambrian-audio | Bucket name |
| Storage__AccessKey | When S3/R2 | - | Access key |
| Storage__SecretKey | When S3/R2 | - | Secret key |
| Email__Provider | No | console | console or smtp |
| Email__SmtpHost | When SMTP | - | SMTP server host |
| Email__SmtpPort | When SMTP | 587 | SMTP port |
| Email__SmtpUser | When SMTP | - | SMTP username |
| Email__SmtpPass | When SMTP | - | SMTP password |
| App__FrontendUrl | Yes | - | Frontend origin for Stripe return URLs |
| App__CorsOrigins | Yes | - | Comma-separated allowed origins |
| App__VercelProjectSlug | Staging only | - | Allows Vercel preview CORS |
| RateLimiting__GlobalPermitLimit | No | 100 | Requests/min per IP |
| RateLimiting__AuthPermitLimit | No | 10 | Auth requests/min per IP |

### Frontend (Vercel)

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| VITE_BACKEND_API | Yes | staging Render URL | Backend API origin |
| VITE_AUTH_API | No | same as VITE_BACKEND_API | Auth API origin (if split) |
| VITE_DEV_BYPASS_AUTH | No | false | Skip auth in local dev |

---

## 4. File Storage Note

Currently using Storage__Provider=local which stores files in wwwroot/uploads inside the container. **Files are lost on every Render redeploy.** For production, switch to S3 or Cloudflare R2:

    Storage__Provider=r2
    Storage__Endpoint=https://<account-id>.r2.cloudflarestorage.com
    Storage__Bucket=cambrian-audio
    Storage__AccessKey=<R2 access key>
    Storage__SecretKey=<R2 secret key>

---

## 5. Quick Deploy Checklist

### Staging
- [ ] Push backend repo to main
- [ ] Create Render Blueprint from render.yaml
- [ ] Set Jwt__Key, Stripe__SecretKey, Stripe__WebhookSecret in Render dashboard
- [ ] Verify https://cambrian-api-staging.onrender.com/health returns OK
- [ ] Set VITE_BACKEND_API in Vercel Preview environment
- [ ] Push frontend repo - Vercel auto-deploys
- [ ] Verify https://cambrian-test.vercel.app loads and API works
- [ ] Create Stripe webhook for staging endpoint

### Production (when ready)
- [ ] Uncomment production blocks in render.yaml
- [ ] Set production env vars in Render dashboard (live Stripe keys, separate JWT secret)
- [ ] Set VITE_BACKEND_API in Vercel Production environment
- [ ] Add custom domain in Vercel (cambrianmusic.com)
- [ ] Switch Storage__Provider to s3 or r2 for persistent file storage
- [ ] Create Stripe live webhook endpoint
- [ ] Change Stripe to live mode in production