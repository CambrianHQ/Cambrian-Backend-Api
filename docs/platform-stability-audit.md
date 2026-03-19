# Platform Stability Audit Report

**Date:** 2026-03-19  
**Branch:** `cursor/platform-stability-audit-7360`  
**Scope:** System health, startup configuration, and environment validation

---

## 1. `src/Cambrian.Api/Program.cs` — FULL CONTENTS

```csharp
using System.Text;
using System.Threading.RateLimiting;
using Cambrian.Api;
using Cambrian.Api.Common;
using Cambrian.Api.Middleware;
using Microsoft.AspNetCore.Mvc;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Stripe;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// --- TEMPORARY: generate controller stubs from OpenAPI spec ---
if (args.Contains("--generate"))
{
    OpenApiControllerGenerator.Run();
    return;
}
// --- END TEMPORARY ---

const string TestingEnvironment = "Testing";

// Database
var connectionString = builder.ResolveConnectionString();
builder.Services.AddDbContext<CambrianDbContext>(options =>
    options.UseNpgsql(connectionString));

// Identity
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredLength = 8;
        options.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<CambrianDbContext>()
    .AddTokenProvider<Microsoft.AspNetCore.Identity.DataProtectorTokenProvider<ApplicationUser>>(
        Microsoft.AspNetCore.Identity.TokenOptions.DefaultProvider);

// Validate secrets (JWT, Stripe, FrontendUrl)
var (jwtKey, _) = builder.ValidateSecrets();

// JWT Authentication
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "cambrian-api",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "cambrian-client",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddMemoryCache();
builder.Services.AddControllers();

// Raise the multipart form body limit for audio uploads
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 150 * 1024 * 1024; // 150 MB
    o.ValueLengthLimit        = 150 * 1024 * 1024;
    o.ValueCountLimit         = 20;
});
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 150 * 1024 * 1024; // 150 MB
});
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .SelectMany(e => e.Value!.Errors.Select(x => x.ErrorMessage))
            .ToList();
        var response = ApiResponse.Fail(string.Join(" | ", errors));
        return new BadRequestObjectResult(response);
    };
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Rate Limiting
var globalLimit = builder.Configuration.GetValue("RateLimiting:GlobalPermitLimit", 100);
var authLimit = builder.Configuration.GetValue("RateLimiting:AuthPermitLimit", 10);
if (builder.Environment.EnvironmentName == TestingEnvironment)
{
    globalLimit = int.MaxValue;
    authLimit = int.MaxValue;
}
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = globalLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.AddPolicy("auth", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// CORS
builder.AddCorsPolicy();

// Services
builder.Services.AddScoped<IAuthService, AuthService>();
// ... (all service registrations)
builder.Services.AddSingleton<IPaymentGateway, StripeFacade>();
builder.AddStorageProvider();
builder.AddEmailProvider();

var app = builder.Build();

if (app.Environment.IsDevelopment() || app.Environment.EnvironmentName == "Staging")
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<ExceptionMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();

// Security headers
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["X-XSS-Protection"] = "0";
    if (!app.Environment.IsDevelopment())
        context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    await next();
});

app.UseCors();

// Serve static files — block direct access to uploaded audio
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.File.PhysicalPath ?? "";
        if (path.Contains("uploads") && !path.Contains("covers"))
        {
            ctx.Context.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Context.Response.ContentLength = 0;
            ctx.Context.Response.Body = Stream.Null;
        }
    }
});

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

await app.RunMigrationsAsync();
await app.SeedDataAsync();
await app.RunAsync();

// Expose the implicit Program class for WebApplicationFactory<Program> in integration tests
public partial class Program
{
    protected Program() { }
}
```

### Issues Found in Program.cs

| # | Severity | Issue | Details |
|---|----------|-------|---------|
| 1 | **LOW** | Swagger exposed on Staging | Line 180: `app.Environment.IsDevelopment() \|\| app.Environment.EnvironmentName == "Staging"` enables Swagger UI in staging. This is acceptable for debugging but could leak internal API structure if staging is publicly accessible. |
| 2 | **INFO** | `--generate` codegen entrypoint in production binary | Lines 19-24: The `OpenApiControllerGenerator.Run()` path is reachable in the production binary. Low risk (requires explicit CLI flag), but unnecessary code in the final image. |
| 3 | **LOW** | Static file audio blocking uses `string.Contains` | Lines 210-211: `path.Contains("uploads") && !path.Contains("covers")` is fragile — case-sensitive, OS-dependent path separators, and any file whose path contains the substring "covers" (even in a different directory tree) would be allowed through. |
| 4 | **OK** | Middleware ordering is correct | ExceptionMiddleware → RequestLogging → Security headers → CORS → StaticFiles → RateLimiter → Auth → Controllers. This is sound. |
| 5 | **OK** | No hardcoded credentials | JWT key, Stripe keys, DB connection string all resolved from config/env. |

---

## 2. `src/Cambrian.Api/appsettings.json` — FULL CONTENTS

```json
{
  "ConnectionStrings": {
    "DefaultConnection": ""
  },
  "App": {
    "FrontendUrl": "",
    "CorsOrigins": "",
    "VercelProjectSlug": "",
    "CloudflarePagesSlug": ""
  },
  "Admin": {
    "Email": "",
    "Password": ""
  },
  "Jwt": {
    "Key": "",
    "Issuer": "cambrian-api",
    "Audience": "cambrian-client"
  },
  "Stripe": {
    "SecretKey": "",
    "WebhookSecret": ""
  },
  "Storage": {
    "Provider": "local",
    "Endpoint": "",
    "Bucket": "cambrian-audio",
    "AccessKey": "",
    "SecretKey": "",
    "Region": "auto",
    "UsePathStyle": true,
    "LocalPath": "wwwroot/uploads",
    "PublicUrl": ""
  },
  "Email": {
    "Provider": "console",
    "FromAddress": "noreply@cambrianmusic.com",
    "FromName": "Cambrian Music",
    "SmtpHost": "",
    "SmtpPort": 587,
    "SmtpUser": "",
    "SmtpPass": ""
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

### Issues Found in appsettings.json

| # | Severity | Issue | Details |
|---|----------|-------|---------|
| 1 | **OK** | All secrets are empty strings | Correct — no credentials are hardcoded in the base config. Values come from env vars or user-secrets at runtime. |
| 2 | **OK** | `Storage:Provider` defaults to `local` | Safe — `StartupExtensions.AddStorageProvider()` throws in Production if provider is `local`. |
| 3 | **OK** | `Email:Provider` defaults to `console` | Safe — `StartupExtensions.AddEmailProvider()` throws in Production if provider is `console`. |
| 4 | **LOW** | `Admin:Email` and `Admin:Password` keys present | Empty values are fine, but their presence in checked-in config is a minor hygiene concern. The seed logic correctly no-ops when these are empty. |
| 5 | **INFO** | No `RateLimiting` section in base config | Rate limits fall back to hardcoded defaults in Program.cs (100 global, 10 auth). This is correct but could be made more explicit. |

---

## 3. `src/Cambrian.Api/appsettings.Development.json` — Summary

Development overrides. Sets `App:FrontendUrl` to `http://localhost:5173` and `App:CorsOrigins` to `http://localhost:5173,http://localhost:5174`. Raises rate limits to 500/200. Uses `local` storage and `console` email.

### Issues Found

| # | Severity | Issue | Details |
|---|----------|-------|---------|
| 1 | **OK** | localhost URLs only in Development config | These values are only loaded when `ASPNETCORE_ENVIRONMENT=Development`. No leakage risk. |
| 2 | **OK** | No credentials hardcoded | All sensitive keys remain empty. |

---

## 4. `.env.example` — Summary

Template file with placeholder values and documentation comments.

### Issues Found

| # | Severity | Issue | Details |
|---|----------|-------|---------|
| 1 | **MEDIUM** | `ASPNETCORE_ENVIRONMENT=Staging` as default | The template defaults to `Staging` instead of `Development`. If a developer copies this to `.env` without editing, their local environment will behave as Staging (e.g., no localhost CORS origins added by default, Swagger still enabled, but no dev-mode error details). |
| 2 | **LOW** | `POSTGRES_PASSWORD=cambrian` hardcoded | Acceptable for local dev/docker only. The comment context makes this clear. |
| 3 | **LOW** | Placeholder `JWT_KEY=your-production-jwt-secret-min-32-chars` | 40 chars, so passes the ≥32 validation, meaning a developer could accidentally deploy with this placeholder. The key is obviously fake but technically valid. |
| 4 | **LOW** | `STRIPE_SECRET_KEY=sk_test_placeholder` | Will fail Stripe API calls at runtime but passes startup validation (non-empty, starts with `sk_test_`). |
| 5 | **INFO** | `FRONTEND_URL` points to staging Vercel deploy | `https://cambrian-test.vercel.app` — appropriate for the staging default in the example. |

---

## 5. `docker-compose.yml` — Summary

Two services: `db` (Postgres 16 Alpine) and `api` (built from Dockerfile). API depends on DB health check. All environment variables are passed through from the `.env` file with sensible defaults.

### Issues Found

| # | Severity | Issue | Details |
|---|----------|-------|---------|
| 1 | **OK** | `depends_on: condition: service_healthy` | Correct — API won't start until Postgres passes `pg_isready`. |
| 2 | **OK** | Env vars use `${VAR:-default}` pattern | Sensible defaults; no credentials hardcoded beyond the dev Postgres password. |
| 3 | **LOW** | `Jwt__Key: "${JWT_KEY}"` has no default | If `.env` doesn't set `JWT_KEY`, this will be empty, and the app will throw `InvalidOperationException` at startup. This is actually correct fail-fast behavior, but could be confusing for first-time setup. |
| 4 | **LOW** | DB port `5432` exposed to host | Standard for local dev. Should not be in a production compose file (and this isn't used in production — Render handles deployment). |
| 5 | **INFO** | `version: '3.9'` is deprecated | Docker Compose v2 ignores this field. No functional impact. |

---

## 6. `render.yaml` — Summary

Render Infrastructure-as-Code blueprint defining staging and production databases and web services. Staging uses free tier; production uses `starter` plan with `basic-256mb` database.

### Issues Found

| # | Severity | Issue | Details |
|---|----------|-------|---------|
| 1 | **OK** | All secrets use `sync: false` | JWT key, Stripe keys, storage credentials, admin credentials are all marked `sync: false` (must be set in Render dashboard, not committed). |
| 2 | **MEDIUM** | Staging CORS includes `http://localhost:5173` | Line 103: `App__CorsOrigins` for staging includes `http://localhost:5173`. This allows any local dev environment to make cross-origin requests to the staging API. If staging processes real test data, this is a minor security concern. |
| 3 | **LOW** | Production `App__VercelProjectSlug` is empty string | Line 181: Set to `""`. This is correct — it disables Vercel preview CORS matching in production. |
| 4 | **OK** | Health check path `/health` configured | Both staging and production define `healthCheckPath: /health`. |
| 5 | **OK** | Build filters restrict rebuilds to relevant paths | `src/**`, `Dockerfile`, `Cambrian.sln`. |
| 6 | **INFO** | Staging database on free plan | Free-tier Render Postgres databases spin down after inactivity and have limited storage. Acceptable for staging. |

---

## 7. `Dockerfile` — Summary

Multi-stage build: SDK 8.0 for build, ASP.NET 8.0 runtime for final image. Uses shell-form `ENTRYPOINT` to expand `$PORT` at runtime.

### Issues Found

| # | Severity | Issue | Details |
|---|----------|-------|---------|
| 1 | **OK** | No credentials in image | All config comes from environment variables at runtime. |
| 2 | **LOW** | Shell-form ENTRYPOINT prevents graceful shutdown signals | `ENTRYPOINT sh -c "..."` runs the app as a child of `sh`, meaning `SIGTERM` from Docker/Render goes to `sh`, not to `dotnet`. The .NET process may not receive graceful shutdown signals. Consider using `exec` inside the shell command: `ENTRYPOINT sh -c "exec dotnet Cambrian.Api.dll --urls=http://+:$PORT"` or set `ASPNETCORE_URLS` as an `ENV` and use exec form. |
| 3 | **OK** | `ENV PORT=8080` with Render override | Render injects `PORT=10000`. Docker Compose maps 8080. Both work correctly. |
| 4 | **INFO** | No `.dockerignore` referenced but mentioned in comments | Line 4 mentions `.dockerignore` — should be verified to exclude test projects, `.git`, `node_modules`, etc. |

---

## 8. Middleware Files — Summary

### `ExceptionMiddleware.cs`

Global exception handler that catches unhandled exceptions, logs them, and returns a structured `ApiResponse` JSON body.

| # | Severity | Issue | Details |
|---|----------|-------|---------|
| 1 | **OK** | Production error messages are sanitized | Line 44: 500 errors in production return `"An unexpected error occurred."` instead of the exception message. |
| 2 | **LOW** | Non-500 errors leak exception messages in production | Lines 37-41: `UnauthorizedAccessException`, `KeyNotFoundException`, `ArgumentException`, and `InvalidOperationException` all return `ex.Message` even in production. If these exceptions contain internal details (e.g., table names, query info), they will be exposed. |
| 3 | **INFO** | No request body is disposed/drained on error | If the request body was partially read when the exception occurred, the connection may not be cleanly closed. This is a minor edge case handled by Kestrel. |
| 4 | **OK** | Response headers already sent check is missing | If `context.Response.HasStarted` is true (e.g., streaming response), setting `StatusCode` and writing JSON will throw. This middleware is placed first in the pipeline, so it's unlikely but possible for streaming endpoints. |

### `RequestLoggingMiddleware.cs`

Adds request correlation IDs and logs HTTP method, path, status code, elapsed time, and user ID.

| # | Severity | Issue | Details |
|---|----------|-------|---------|
| 1 | **OK** | Clean implementation | Generates a 12-char request ID, attaches it to response headers, logs timing. |
| 2 | **LOW** | Does not log on exception | If `_next(context)` throws, the log line at line 30 is never reached. The ExceptionMiddleware above it catches the error, but the request timing is lost. The middleware does not use try/finally. |
| 3 | **INFO** | Request ID truncated to 12 chars | `Guid.NewGuid().ToString("N")[..12]` — 12 hex chars = 48 bits of entropy. Sufficient for correlation but could collide at very high request volumes. |

### `RequireCreatorTierAttribute.cs`

Action filter that checks whether the authenticated user has a "creator" or "pro" tier.

| # | Severity | Issue | Details |
|---|----------|-------|---------|
| 1 | **OK** | Falls back to DB for stale JWT claims | If the JWT "tier" claim is outdated (e.g., user upgraded), it queries UserManager. |
| 2 | **OK** | Returns structured error response | Uses `ApiResponse.Fail()` with a helpful message and 403 status. |
| 3 | **INFO** | No caching of DB lookup | Every request with a stale/missing tier claim hits the database. Could be mitigated with short-lived caching, but the impact is low since most tokens will have the claim. |

---

## 9. Common Files — Summary

### `ApiResponse.cs`

Generic and non-generic API response envelope with `Success`, `Data`, `Message`, and `Error` fields.

| # | Severity | Issue | Details |
|---|----------|-------|---------|
| 1 | **OK** | Clean, minimal implementation | Provides consistent response structure across all endpoints. |
| 2 | **INFO** | No pagination support in envelope | If paginated responses are needed, they would need to be wrapped in `Data`. Not a bug, just an architectural note. |

---

## 10. `StartupExtensions.cs` — Summary (critical startup logic)

This file contains the most important startup validation and configuration logic.

### Issues Found

| # | Severity | Issue | Details |
|---|----------|-------|---------|
| 1 | **MEDIUM** | Connection string logged to console | Line 32: `Console.WriteLine($"[Startup] DB connection source: ...")` — the source type is logged (URI vs ADO.NET), not the actual string. **OK.** But line 41: `Console.WriteLine($"[Startup] Parsed DB URI → Host={uri.Host}, Port={port}, DB={uri.AbsolutePath.TrimStart('/')}")` logs the host and database name. This is generally acceptable for operational logs but could be an info leak if logs are publicly accessible. |
| 2 | **MEDIUM** | JWT key length logged | Line 56: `Console.WriteLine($"[Startup] JWT key present: {!string.IsNullOrWhiteSpace(jwtKey)} (len={jwtKey.Length})")` — logging the key length reveals information about the key. Minor risk. |
| 3 | **OK** | Production fail-fast for missing JWT key | Lines 61-68: Throws `InvalidOperationException` if JWT key is missing (except in Testing env). |
| 4 | **OK** | Production fail-fast for local storage | Lines 110-112: Throws if `Storage:Provider` is `local` in Production. |
| 5 | **OK** | Production fail-fast for console email | Lines 133-135: Throws if `Email:Provider` is `console` in Production. |
| 6 | **OK** | Stripe test key rejected in Production | Lines 262-264: Throws if a `sk_test_` key is used in Production. |
| 7 | **LOW** | Non-prod live Stripe key only warns | Line 266: If a non-prod environment uses `sk_live_`, it only prints a warning. Real charges could be processed. |
| 8 | **MEDIUM** | Admin password logged indirectly | Line 287: `Console.WriteLine($"[Seed] Admin config — email={adminEmail ?? "(null)"}, passwordLength={adminPassword?.Length ?? 0}")` — logs the admin email in plaintext and the password length. The email is PII; the password length is a minor information leak. |
| 9 | **LOW** | Migration errors swallowed in non-production | Lines 219-223: In non-production, migration exceptions are caught and only logged to console. The app continues to start with a potentially outdated schema. This could cause confusing runtime errors. |
| 10 | **OK** | CORS policy well-structured | Origins are environment-specific, Vercel/Cloudflare preview matching is opt-in via config slugs, and production requires explicit `FrontendUrl`. |
| 11 | **LOW** | Hardcoded production CORS origins | Lines 155-157: `"https://cambrianmusic.com"` and `"https://www.cambrianmusic.com"` are hardcoded as additional production origins. If the domain changes, code must be updated. These duplicate what's in `render.yaml` via `App__CorsOrigins`. |
| 12 | **LOW** | Hardcoded staging CORS origins | Lines 158-159: `"https://staging.cambrianmusic.com"` and `"https://api-staging.cambrianmusic.com"` are hardcoded for staging. Same concern as above. |
| 13 | **OK** | `DATABASE_URL` URI-to-ADO.NET conversion | Lines 35-42: Correctly converts Render's `postgres://` URI format to Npgsql connection string with SSL mode. |

---

## Consolidated Findings by Category

### Hardcoded URLs or Credentials

- **No credentials are hardcoded** in any production-path code. All secrets (JWT, Stripe, DB, Storage, Email, Admin) are resolved from environment variables or config overrides.
- **Hardcoded domain names** exist in `StartupExtensions.cs` (production: `cambrianmusic.com`, staging: `staging.cambrianmusic.com`) as fallback CORS origins. These duplicate `render.yaml` config values and create a maintenance burden.
- **Hardcoded test fallback** in `ResolveConnectionString` for Testing environment: `Host=localhost;Port=5432;Database=cambrian_test;Username=postgres;Password=postgres` — acceptable for CI/test only.

### Localhost/Staging Leakage

- **No localhost URLs leak into production.** `appsettings.Development.json` localhost values only load in Development environment. The CORS logic adds localhost origins only in `IsDevelopment()`.
- **Staging CORS allows localhost** via `render.yaml` (`http://localhost:5173` in staging CorsOrigins). Minor concern if staging processes sensitive test data.
- **`.env.example` defaults to `Staging`** environment, which could cause confusion for new developers.

### Missing or Incorrect Environment Variable References

- **`DATABASE_URL` and `ConnectionStrings__DefaultConnection` both supported** — no gaps.
- **JWT key resolution checks** `Jwt:Key` (config), `Jwt__Key` (env), and `JWT_KEY` (env) — thorough.
- **No missing references found.** All env vars referenced in `docker-compose.yml` and `render.yaml` are consumed by the application.

### Configuration Issues That Could Cause Instability

1. **Shell-form ENTRYPOINT in Dockerfile** — may prevent graceful shutdown.
2. **Migration errors swallowed in non-production** — app starts with stale schema.
3. **No connection pool configuration** — Npgsql defaults apply; no `MaxPoolSize`, `ConnectionLifetime`, or `Keepalive` tuning.
4. **No health check endpoint verification** — `render.yaml` references `/health` but the health controller registration isn't visible in Program.cs (registered via `IHealthService` service).

### Error Handling Gaps

1. **ExceptionMiddleware doesn't check `Response.HasStarted`** — could throw on streaming responses.
2. **Non-500 errors expose `ex.Message` in production** — potential internal detail leakage.
3. **RequestLoggingMiddleware doesn't log on exception paths** — timing data lost for failed requests.

### CORS Misconfiguration

- **Generally well-configured.** Environment-specific origins, Vercel/CF preview matching, `AllowCredentials()` correctly paired with explicit origin list (not `AllowAnyOrigin`).
- **Minor: staging allows localhost.** Could be tightened.
- **Minor: hardcoded domain origins create maintenance burden** alongside config-based origins.

### Database Connection String Handling

- **Well-implemented.** Supports ADO.NET format, Render's `postgres://` URI format with automatic conversion, SSL enforcement for URI-based connections, and `DATABASE_URL` environment variable.
- **No connection resilience configuration** — no retry policy (`EnableRetryOnFailure`) configured on `UseNpgsql()`. Transient database failures will surface as unhandled exceptions.

---

## Priority Recommendations

| Priority | Recommendation | Effort |
|----------|---------------|--------|
| **P1** | Add `exec` to Dockerfile ENTRYPOINT for graceful shutdown: `sh -c "exec dotnet Cambrian.Api.dll"` | 1 line |
| **P1** | Add `Response.HasStarted` guard in `ExceptionMiddleware` | 3 lines |
| **P2** | Add Npgsql retry policy: `.UseNpgsql(conn, o => o.EnableRetryOnFailure())` | 1 line |
| **P2** | Sanitize `ex.Message` for non-500 errors in production (ExceptionMiddleware) | 5 lines |
| **P2** | Wrap `RequestLoggingMiddleware._next()` in try/finally to always log timing | 5 lines |
| **P3** | Remove hardcoded domain origins from `StartupExtensions.cs`; rely solely on config | 10 lines |
| **P3** | Change `.env.example` default to `ASPNETCORE_ENVIRONMENT=Development` | 1 line |
| **P3** | Remove staging `localhost` from `render.yaml` CORS origins | 1 line |
| **P3** | Add connection pool tuning (`MaxPoolSize`, etc.) for production database | Config change |
| **P4** | Stop logging admin email and JWT key length in startup | 2 lines |
