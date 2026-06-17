# Environment Contract — Cambrian API

Every configuration value the API reads, grouped by concern, with whether it is
**required** and where it is set **local** vs **deploy**. Config binds in this
precedence (later wins): `appsettings.json` → `appsettings.{Environment}.json`
→ environment variables → user-secrets (Development).

`:` in a key maps to `__` (double underscore) in an environment variable —
e.g. `Jwt:Key` is the env var `Jwt__Key`. A few flags also accept a flat
SCREAMING_SNAKE alias (noted inline).

Legend: **R** = required, **R(prod)** = required in Production only, **O** = optional (has a default).

---

## Database
| Key | Req | Local | Deploy | Notes |
|-----|-----|-------|--------|-------|
| `ConnectionStrings:DefaultConnection` | R | docker-compose Postgres (`Host=localhost;Port=5433;Database=cambrian;Username=cambrian;Password=cambrian`) | Render Postgres | ADO.NET form, or a `postgres://` URI (auto-parsed). `DATABASE_URL` is accepted as a fallback. |

## JWT / Auth
| Key | Req | Local | Deploy | Notes |
|-----|-----|-------|--------|-------|
| `Jwt:Key` | R | `.env` / user-secrets (≥32 chars) | secret | Signing key; startup fails if missing/short. |
| `Jwt:Issuer` | R | `cambrian-api` | `cambrian-api` | Validated at startup. |
| `Jwt:Audience` | R | `cambrian-client` | `cambrian-client` | Validated at startup. |
| `Google:ClientId` | O | empty | set if Google sign-in enabled | Google OAuth client id. |

## Stripe / Billing
| Key | Req | Local | Deploy | Notes |
|-----|-----|-------|--------|-------|
| `Stripe:SecretKey` | R(prod) | `sk_test_…` (else DevelopmentPaymentGateway) | `sk_live_…`/`sk_test_…` | Empty in dev falls back to a fake gateway. |
| `Stripe:WebhookSecret` | R(prod) | the `whsec_…` printed by `stripe listen` | `whsec_…` from the Stripe dashboard endpoint | **Local:** only the CLI-printed secret verifies; the dashboard secret is wrong locally. |
| `Stripe:ConnectWebhookSecret` | R(prod) | the `whsec_…` printed by `stripe listen --forward-to localhost:8080/webhook/stripe/connect` | `whsec_…` from the Stripe Connect webhook endpoint | Separate signing secret for tips and fan subscriptions. |
| `Stripe:Prices:Creator` | R(billing) | `price_…` for the Creator plan | `price_…` | Must be a real Price id in the **same** Stripe account as `Stripe:SecretKey`. |
| `Stripe:Prices:Pro` | R(billing) | `price_…` for the Pro plan | `price_…` | Same-account requirement as above. |

> Checkout-session creation fails with `No such price` if the price ids are not
> in the secret key's account — verify all three move together.

## Checkout kill switch (residue #6)
| Key | Req | Default | Notes |
|-----|-----|---------|-------|
| `Checkout:Enabled` / `CHECKOUT_ENABLED` | O | `true` | Set to `false` to make every checkout-session endpoint return **503** `{ error: "checkout_disabled" }`. The Stripe Customer Portal stays available so subscribers can still cancel. |
| `Checkout:RequireSubscription` | O | `true` (prod), `false` (dev) | Require an active subscription to download tracks. |

## Storage
| Key | Req | Default | Notes |
|-----|-----|---------|-------|
| `Storage:Provider` | R | `local` | `local` \| `s3` \| `r2`. |
| `Storage:LocalPath` | R(local) | `wwwroot/uploads` | Disk path for the `local` provider. |
| `Storage:Endpoint` | R(s3/r2) | empty | S3/R2 endpoint URL. |
| `Storage:Bucket` | R(s3/r2) | `cambrian-audio` | Bucket name. |
| `Storage:AccessKey` / `Storage:SecretKey` | R(s3/r2) | empty | Object-store credentials. |
| `Storage:Region` | O | `auto` | `auto` for R2. |
| `Storage:UsePathStyle` | O | `true` | Required `true` for R2. |
| `Storage:PublicUrl` | R(s3/r2) | empty | Public base URL for cover-art delivery. |

## Email / SMS
| Key | Req | Default | Notes |
|-----|-----|---------|-------|
| `Email:Provider` | O | `console` | `console` \| `smtp` \| `resend` \| `sendgrid`. |
| `Email:FromAddress` / `Email:FromName` | O | `noreply@cambrianmusic.com` / `Cambrian Music` | Sender identity. |
| `Email:Smtp{Host,Port,User,Pass}` | R(smtp) | — / `587` | SMTP settings. |
| `Email:ResendApiKey` / `Email:ResendWebhookSecret` | R(resend) | empty | Resend key + Svix `whsec_…`. |
| `Email:SendGridApiKey` | R(sendgrid) | empty | SendGrid key. |
| `Sms:Provider` | O | `console` | Only `console` implemented. |

## App / CORS
| Key | Req | Local | Deploy | Notes |
|-----|-----|-------|--------|-------|
| `App:FrontendUrl` | R(prod) | `http://localhost:3000` | `https://cambrianmusic.com` | Used for Stripe return URLs + links. |
| `App:CorsOrigins` | O | `http://localhost:3000,http://localhost:5173,http://localhost:5174` | comma-separated origins | Merged with built-in dev/preview origins. |
| `App:VercelProjectSlug` / `App:CloudflarePagesSlug` | O | empty | slugs | Preview-URL CORS matching. |

## Admin / Seeding (non-production)
| Key | Req | Notes |
|-----|-----|-------|
| `Admin:Email` / `Admin:Password` | O | Seeds an admin account on startup when set. |
| `SeedDemoUsers:Password` | O | Shared password for the demo users (`aiden…juniper@cambrianmusic.com`). Skipped in Production. |
| `SeedStagingData` | O | Seed staging tracks/data (dev/staging). |

## Rate limiting
| Key | Default | Notes |
|-----|---------|-------|
| `RateLimiting:GlobalPermitLimit` | `100` (dev/staging `500`) | Global req/min. |
| `RateLimiting:AuthPermitLimit` | `10` (dev `200`, staging `100`) | Auth req/min. |

## Mastering / Release Ready
| Key | Default | Notes |
|-----|---------|-------|
| `Mastering:Engine` | `ffmpeg` | `ffmpeg` \| `tonn`. |
| `Mastering:TargetLufs` / `Mastering:TargetTruePeakDbtp` | `-14.0` / `-1.0` | Loudness/true-peak targets. |
| `Mastering:JobTimeoutSeconds` | `480` | Per-job ceiling. |
| `Mastering:Ffmpeg:FfmpegPath` | `ffmpeg` | Binary path. |
| `Mastering:Tonn:{BaseUrl,ApiKey,DefaultMusicalStyle,PollTimeoutSeconds}` | `https://tonn.roexaudio.com` / empty / `POP` / `180` | RoEx Tonn engine (needs API key). |

## Provenance / anchoring
| Key | Default | Notes |
|-----|---------|-------|
| `Provenance:Signing:PrivateKeyPem` | empty | EC P-256 PKCS#8 PEM; **required in prod**. |
| `Provenance:Anchor:Provider` | `noop` | `noop` \| `evm`. |
| `Provenance:Anchor:{JobEnabled,IntervalSeconds,MaxBatchSize,Chain,ChainId}` | `false`/`300`/`500`/`base`/`8453` | Batch anchoring worker config. |
| `Provenance:Anchor:{RpcUrl,PrivateKey}` | empty | Required when `Provider=evm`. |

## Observability
| Key | Default | Notes |
|-----|---------|-------|
| `Sentry:Dsn` | empty | Disabled when empty. |
| `Sentry:{SendDefaultPii,AttachStacktrace,TracesSampleRate,MinimumBreadcrumbLevel,MinimumEventLevel}` | `false`/`true`/`0.1`/`Information`/`Warning` | Error-reporting tuning. |
| `GIT_COMMIT` / `RENDER_GIT_COMMIT` | unknown | Surfaced in `GET /healthz` build info. |

---

## Minimal local `.env` (docker-compose stack)
```
POSTGRES_PASSWORD=cambrian
Jwt__Key=cambrian-local-dev-key-minimum-32-chars!!
Jwt__Issuer=cambrian-api
Jwt__Audience=cambrian-client
Stripe__SecretKey=sk_test_…
Stripe__WebhookSecret=whsec_…           # from `stripe listen`
Stripe__ConnectWebhookSecret=whsec_…    # from `stripe listen --forward-to localhost:8080/webhook/stripe/connect`
Stripe__Prices__Creator=price_…          # same Stripe account as SecretKey
Stripe__Prices__Pro=price_…
Storage__Provider=local
Email__Provider=console
App__FrontendUrl=http://localhost:3000
App__CorsOrigins=http://localhost:3000
Admin__Email=admin@cambrian.local
Admin__Password=Admin123!ChangeMe
SeedDemoUsers__Password=Demo123!Pass
# CHECKOUT_ENABLED=false                 # uncomment to kill new checkouts
```

## Health / ops endpoints
- `GET /health` — full status (DB connectivity + counts).
- `GET /healthz` — lightweight keep-warm/liveness probe with build info (version + `GIT_COMMIT`); anonymous, no DB hit.
- `POST /admin/charts/aggregate` — admin-only; recompute the weekly chart on demand (`GET /api/charts/weekly`).
