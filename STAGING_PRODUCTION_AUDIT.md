# Staging vs Production Environment Audit

**Date:** 2026-03-19
**Auditor:** Automated Systems Engineer
**Repository:** Cambrian Backend API (.NET 8 / ASP.NET Core)

---

## Executive Summary

The staging environment is actively deployed on Render. The production environment **has no IaC definition** — its entire `render.yaml` block (DB + API) is commented out. When production goes live, multiple configuration gaps will cause failures in storage, email delivery, CORS, and potentially payments if not addressed.

**Critical findings: 4 | High: 6 | Medium: 5 | Low: 4**

---

## 1. ENVIRONMENT VARIABLES

### 1.1 Production `appsettings.Production.json` is missing entire config sections

| Section | Staging (`appsettings.Staging.json`) | Production (`appsettings.Production.json`) | Severity |
|---------|--------------------------------------|---------------------------------------------|----------|
| `Storage` | Full S3 config (`Provider: "s3"`, `Bucket: "cambrian-audio-staging"`) | **Absent** — falls back to base `appsettings.json` which sets `Provider: "local"` | **CRITICAL** |
| `Email` | `Provider: "resend"`, `FromName: "Cambrian Music (Staging)"` | **Absent** — falls back to base `Provider: "console"` | **HIGH** |
| `RateLimiting` | `GlobalPermitLimit: 500`, `AuthPermitLimit: 100` | **Absent** — code defaults to `100` global, `10` auth | **MEDIUM** |
| `App.VercelProjectSlug` | `"cambrian"` | **Absent** | **LOW** |

**Risk:** If production is launched using only `appsettings.Production.json` without overriding these via environment variables:
- **Storage falls back to `local`** — all uploaded audio files will be stored on the ephemeral container filesystem and **lost on every deploy or restart**. This is a data-loss scenario.
- **Email falls back to `console`** — password resets, welcome emails, and verification codes will be logged to stdout instead of delivered to users. Users cannot recover accounts.
- Rate limiting will use conservative defaults (100/min global, 10/min auth), which is actually appropriate for production.

**Fix:** Add explicit `Storage` and `Email` sections to `appsettings.Production.json`, or ensure these are always set via environment variables in the Render dashboard. The current reliance on env vars with no appsettings fallback is fragile.

---

### 1.2 CORS origins incomplete in production config

| Environment | `App:CorsOrigins` | `App:FrontendUrl` |
|-------------|-------------------|-------------------|
| Staging | `https://staging.cambrianmusic.com,http://localhost:5173` | `https://staging.cambrianmusic.com` |
| Production | `https://cambrianmusic.com` | `https://cambrianmusic.com` |

**Severity: HIGH**

**Issues:**
1. **Staging includes `http://localhost:5173`** in deployed CORS origins. Any developer running the frontend locally can make authenticated requests against the staging API. While not catastrophic, it widens the attack surface and is unusual for a deployed environment.
2. **Production config is missing `https://www.cambrianmusic.com`**. However, `Program.cs` lines 198-200 hardcode both `cambrianmusic.com` and `www.cambrianmusic.com` into `productionOrigins` when `ASPNETCORE_ENVIRONMENT=Production`, so this is **mitigated at runtime**. The config file is still misleading.

**Fix:**
- Remove `http://localhost:5173` from staging `CorsOrigins`.
- Add `https://www.cambrianmusic.com` to production `CorsOrigins` for clarity, even though the hardcoded fallback covers it.

---

### 1.3 Cloudflare Pages slug identical across environments

| Environment | `App:CloudflarePagesSlug` |
|-------------|--------------------------|
| Staging | `cambrian-ciz` |
| Production | `cambrian-ciz` |

**Severity: MEDIUM**

**Risk:** Both staging and production accept CORS requests from **any** `*.pages.dev` subdomain containing `cambrian-ciz`. This means Cloudflare Pages preview deployments (which may be running untested frontend code) can make authenticated API calls against the **production** backend.

**Fix:** Remove `CloudflarePagesSlug` from `appsettings.Production.json` or set it to an empty string. Preview deployments should only target staging.

---

### 1.4 JWT secret fallback applies to staging

**File:** `src/Cambrian.Api/Program.cs` lines 79-88

```csharp
var isNonProd = builder.Environment.IsDevelopment()
    || builder.Environment.EnvironmentName == "Staging"
    || builder.Environment.EnvironmentName == "Testing";
if (string.IsNullOrWhiteSpace(jwtKey))
{
    if (isNonProd)
        jwtKey = "cambrian-dev-secret-key-min-32-chars!!";
    else
        throw new InvalidOperationException(...);
}
```

**Severity: HIGH**

**Risk:** If `Jwt__Key` is not set in the Render dashboard for staging, the API silently falls back to the **published** development secret `cambrian-dev-secret-key-min-32-chars!!`. Anyone who reads this source code can forge valid JWTs for the staging environment, gaining access to any user account.

**Fix:** Remove `"Staging"` from the `isNonProd` check for the JWT fallback, or at minimum log a prominent warning when the fallback is used. Staging should require a real secret just like production.

---

### 1.5 Email provider divergence between staging and production plans

| Environment | Email Provider | Transport |
|-------------|---------------|-----------|
| Staging | `resend` | HTTPS API (Resend) |
| Production (planned, from render.yaml comments) | `smtp` | SMTP (MailKit) |

**Severity: MEDIUM**

**Risk:** Staging and production use completely different email delivery paths. Bugs in the SMTP implementation (`SmtpEmailService`) will not be caught during staging testing because staging uses `ResendEmailService`. These are separate codepaths with different error handling, timeouts, and authentication mechanisms.

**Fix:** Use the same email provider in both environments (preferably Resend for both, since it works reliably on Render), or add integration tests that exercise the SMTP path.

---

## 2. API ENDPOINTS

### 2.1 Hardcoded `localhost:5173` fallback in 4 production services

Multiple services fall back to `http://localhost:5173` if `App:FrontendUrl` is not configured:

| File | Line | Code |
|------|------|------|
| `src/Cambrian.Infrastructure/Stripe/StripeFacade.cs` | 14 | `configuration["App:FrontendUrl"] ?? "http://localhost:5173"` |
| `src/Cambrian.Application/Services/CheckoutService.cs` | 43 | `configuration["App:FrontendUrl"] ?? "http://localhost:5173"` |
| `src/Cambrian.Application/Services/BillingService.cs` | 157 | `string.IsNullOrWhiteSpace(configuredUrl) ? "http://localhost:5173" : ...` |
| `src/Cambrian.Application/Services/CreatorConnectService.cs` | 47 | `_config["App:FrontendUrl"] ?? "http://localhost:5173"` |

**Severity: CRITICAL**

**Risk:** If `App:FrontendUrl` is missing or blank in production, **all Stripe checkout sessions** (track purchases, subscriptions, Stripe Connect onboarding) will redirect users to `http://localhost:5173` after payment. Users complete payment but land on a dead page. Money is collected but the user experience is broken.

**Fix:** These fallbacks should throw at startup in production rather than silently defaulting to localhost. Add a startup validation check similar to the JWT key check:

```csharp
if (builder.Environment.IsProduction() && string.IsNullOrWhiteSpace(frontendUrl))
    throw new InvalidOperationException("App:FrontendUrl must be configured in Production.");
```

---

### 2.2 Staging URL hardcoded in test file

**File:** `tests/Cambrian.Api.Tests/LibraryControllerTests.cs` line 193

```csharp
context.Request.Host = new HostString("cambrian-api-staging.onrender.com");
```

**Severity: LOW**

**Risk:** No runtime impact — this is test-only code. However, if Render service names change, this test would use a stale hostname, potentially masking URL generation bugs.

**Fix:** Extract to a test constant or use a generic hostname like `api.example.com`.

---

### 2.3 Swagger UI exposed in staging, hidden in production

**File:** `src/Cambrian.Api/Program.cs` lines 347-351

```csharp
if (app.Environment.IsDevelopment() || app.Environment.EnvironmentName == "Staging")
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
```

**Severity: LOW**

**Risk:** Intentional and appropriate. Swagger in staging aids testing. Not having it in production reduces attack surface. No action needed.

---

## 3. BUILD & DEPLOY CONFIG

### 3.1 Production infrastructure is entirely commented out in `render.yaml`

**File:** `render.yaml` lines 19-172

The entire production database (`cambrian-db-prod`) and production API service (`cambrian-api`) are commented out.

**Severity: CRITICAL**

**Risk:** When production launches, the infrastructure must be configured **entirely through the Render dashboard** rather than through IaC. This creates:
- No version-controlled record of production configuration
- Risk of configuration drift between what's documented and what's deployed
- No way to reproduce the production environment from code
- Missing variables won't be caught until runtime

**Fix:** Uncomment and finalize the production blocks before launch. At minimum, document every env var that must be manually set.

---

### 3.2 Production deploy workflow lacks smoke test

**File:** `.github/workflows/deploy-production.yml`

The staging and production deploy workflows are structurally identical (build → test → curl webhook), except:
- Production has `environment: production` requiring manual approval (correct).
- Neither workflow runs a post-deploy health check.

**Severity: MEDIUM**

**Risk:** A deployment that passes unit tests but fails at runtime (e.g., due to missing env vars or DB migration errors) won't be caught automatically.

**Fix:** Add a post-deploy step that hits `/health` on the deployed service and fails the workflow if the response is not `200 OK`.

---

### 3.3 Auto-migration runs on every startup in all non-Testing environments

**File:** `src/Cambrian.Api/Program.cs` lines 397-410

```csharp
if (app.Environment.EnvironmentName != "Testing")
{
    migrateDb.Database.Migrate();
}
```

**Severity: HIGH**

**Risk:** An untested or destructive migration will be applied to the production database automatically on the next deploy. There is no migration review gate, no backup step, and no rollback mechanism. If a migration adds a NOT NULL column without a default, the API crashes on startup and may leave the database in a partially migrated state.

**Fix:** Consider disabling auto-migration in production (`IsProduction()` guard) and applying migrations through a dedicated CI step with a backup/rollback plan.

---

## 4. DATABASE / BACKEND CONNECTIONS

### 4.1 No cross-environment database references found

**Status: PASS**

- Staging uses `cambrian-db-staging` → `cambrian_staging` database
- Production plans to use `cambrian-db-prod` → `cambrian_prod` database
- Connection strings are resolved from env vars at startup with no hardcoded host/port
- The `DATABASE_URL` → Npgsql conversion logic in `Program.cs` correctly handles Render's `postgres://` URI format

No risk of staging pointing to production DB or vice versa, **as long as env vars are set correctly in Render dashboard**.

---

### 4.2 Database plans differ

| Environment | Plan | Implication |
|-------------|------|-------------|
| Staging | `free` | 90-day retention, 256MB storage, may be suspended after inactivity |
| Production (planned) | `starter` (paid) | Persistent storage, no suspension |

**Severity: LOW**

This is intentional and appropriate. No action needed.

---

## 5. FEATURE FLAGS / CONDITIONAL LOGIC

### 5.1 `creator_storefront` feature flag seeded as disabled in both environments

**File:** `src/Cambrian.Api/Program.cs` lines 486-505

The `creator_storefront` flag is created with `enabled: false` on first startup. It's toggled via the admin API (`PUT /feature-flags/{name}`). Both environments start with it disabled.

**Severity: LOW**

**Risk:** Feature flag state lives in each environment's database. If an admin enables `creator_storefront` in staging for testing, it doesn't affect production. However, there's no mechanism to sync feature flag state between environments — when production launches, all flags start disabled even if they were battle-tested in staging.

**Fix:** Document the expected feature flag state for production launch. Consider a migration or seed script that sets the desired initial state.

---

### 5.2 Stripe webhook verification differs by environment

**File:** `src/Cambrian.Infrastructure/Stripe/StripeWebhookService.cs` line 35

```csharp
_isDevelopment = env.IsDevelopment() || env.EnvironmentName == "Testing";
```

| Environment | Webhook Signature Verification |
|-------------|-------------------------------|
| Development | Skipped (fallback JSON parse) |
| Testing | Skipped |
| **Staging** | **Enforced** |
| **Production** | **Enforced** |

**Status: PASS** — Both staging and production enforce Stripe webhook signature verification. Unverified webhooks are rejected with a `500` error.

---

### 5.3 Exception detail exposure differs by environment

**File:** `src/Cambrian.Api/Middleware/ExceptionMiddleware.cs` line 20

```csharp
_isProduction = env.IsProduction();
```

| Environment | 500 Error Response |
|-------------|-------------------|
| Development, Staging, Testing | Full `ex.Message` exposed in API response |
| **Production** | Generic `"An unexpected error occurred."` |

**Severity: LOW**

**Risk:** Staging exposes exception messages to API consumers, which could leak internal details (DB schema info, file paths, etc.). This is acceptable for debugging but worth noting.

---

### 5.4 Stripe API key mode cannot be validated from code

**Severity: HIGH**

The code does not validate whether the Stripe key is a test key (`sk_test_`) or live key (`sk_live_`). If a production environment is accidentally configured with a test Stripe key:
- Payments will appear to succeed but no real money changes hands
- Users get "purchased" tracks without paying

If staging is accidentally configured with a live Stripe key:
- Real charges are processed during testing
- Test users are billed real money

**Fix:** Add a startup check:

```csharp
if (builder.Environment.IsProduction() && stripeKey.StartsWith("sk_test_"))
    throw new InvalidOperationException("Production must not use Stripe test keys.");
if (isNonProd && stripeKey.StartsWith("sk_live_"))
    Console.WriteLine("[WARN] Non-production environment is using a LIVE Stripe key!");
```

---

## Summary of All Findings

| # | Finding | Severity | Category |
|---|---------|----------|----------|
| 1.1 | Production missing Storage/Email config — defaults to local/console | **CRITICAL** | Env Vars |
| 2.1 | Four services fall back to `localhost:5173` if FrontendUrl missing | **CRITICAL** | API Endpoints |
| 3.1 | Production infrastructure entirely commented out in render.yaml | **CRITICAL** | Deploy Config |
| 5.4 | No validation of Stripe test vs live key per environment | **CRITICAL** | Feature Flags |
| 1.2 | Staging CORS includes localhost; production config missing www | **HIGH** | Env Vars |
| 1.4 | JWT fallback secret applies to staging (published in source code) | **HIGH** | Env Vars |
| 1.5 | Staging uses Resend, production plans SMTP — different codepaths | **MEDIUM** | Env Vars |
| 3.3 | Auto-migration runs in production with no rollback mechanism | **HIGH** | Deploy Config |
| 5.4 | No Stripe key mode (test/live) validation at startup | **HIGH** | Feature Flags |
| 1.3 | Cloudflare Pages slug same in both envs — previews hit production | **MEDIUM** | Env Vars |
| 3.2 | No post-deploy health check in CI/CD pipelines | **MEDIUM** | Deploy Config |
| 1.2a | Staging CorsOrigins includes localhost:5173 | **MEDIUM** | Env Vars |
| 5.1 | Feature flags not synced between environments | **LOW** | Feature Flags |
| 5.3 | Staging exposes exception details in API responses | **LOW** | Feature Flags |
| 2.2 | Staging hostname hardcoded in test file | **LOW** | API Endpoints |
| 2.3 | Swagger enabled in staging, disabled in production (intentional) | **LOW** | API Endpoints |
| 4.2 | Different DB plans (free vs starter) — intentional | **LOW** | Database |

---

## Recommended Priority Actions (Before Production Launch)

1. **Uncomment and finalize production blocks in `render.yaml`** — no production infrastructure exists in code.
2. **Add `Storage` and `Email` sections to `appsettings.Production.json`** with production-appropriate defaults (e.g., `Provider: "s3"`, `Provider: "resend"` or `"smtp"`).
3. **Add startup validation for `App:FrontendUrl`** — throw in production if missing instead of falling back to localhost.
4. **Add Stripe key mode validation** — prevent test keys in production and warn on live keys in staging.
5. **Remove `"Staging"` from the JWT fallback `isNonProd` check** — staging should require a real secret.
6. **Remove `CloudflarePagesSlug` from production config** — preview deployments should not have CORS access to production.
7. **Remove `localhost:5173` from staging CORS origins** — deployed environments should not accept local origins.
8. **Consider disabling auto-migration in production** — apply migrations through a controlled process with backups.
