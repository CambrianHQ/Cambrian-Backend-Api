using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Cambrian.Api;
using Cambrian.Api.Common;
using Cambrian.Api.Middleware;
using Cambrian.Application.Configuration;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Stripe;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;

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
builder.ValidateSecrets();
builder.Services
    .AddOptions<JwtSettings>()
    .Bind(builder.Configuration.GetSection("Jwt"))
    .PostConfigure(settings =>
    {
        if (string.IsNullOrWhiteSpace(settings.Key) || settings.Key.Length < 32)
            throw new InvalidOperationException("JWT Key must be at least 32 characters.");

        if (string.IsNullOrWhiteSpace(settings.Issuer))
            throw new InvalidOperationException("JWT Issuer is required.");

        if (string.IsNullOrWhiteSpace(settings.Audience))
            throw new InvalidOperationException("JWT Audience is required.");
    })
    .ValidateOnStart();

builder.Services
    .AddOptions<GoogleSettings>()
    .Bind(builder.Configuration.GetSection("Google"));

Console.WriteLine("[Startup] Governance contract version: 2.1.0 — see policy/POLICY.md, contracts/API_CONTRACTS.md");

// Dual authentication: accept either a Bearer JWT (Authorization header)
// or an HttpOnly cookie that carries the same JWT (cookie transport).
// The SmartScheme policy scheme selects the correct handler at request time.
const string SmartScheme = "SmartScheme";
const string CookieSchemeName = "Cookies";

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = SmartScheme;
    options.DefaultChallengeScheme    = SmartScheme;
})
.AddPolicyScheme(SmartScheme, "JWT or Cookie", options =>
{
    options.ForwardDefaultSelector = context =>
    {
        // If an Authorization header is present, validate as Bearer JWT.
        // Otherwise fall back to cookie (which also carries a JWT).
        return context.Request.Headers.ContainsKey("Authorization")
            ? JwtBearerDefaults.AuthenticationScheme
            : CookieSchemeName;
    };
})
.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, _ => { })
.AddCookie(CookieSchemeName, options =>
{
    options.Cookie.Name     = "auth_token";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy  = CookieSecurePolicy.SameAsRequest; // Always in prod via HTTPS
    options.Cookie.SameSite = SameSiteMode.Lax;  // Lax allows cross-site top-level nav (Stripe redirect)
    options.ExpireTimeSpan  = TimeSpan.FromDays(7);
    options.SlidingExpiration = false;

    // Cookie auth reads the token from the cookie and validates it as a JWT.
    // This makes cookie and bearer interchangeable — same token, different transport.
    options.Events = new CookieAuthenticationEvents
    {
        // Return 401/403 directly — API clients cannot follow login redirects.
        OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        },
        OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        },
        OnValidatePrincipal = async ctx =>
        {
            // Extract the JWT stored in the cookie and send it through JWT validation.
            // This reuses the existing JwtBearerOptions without duplicating key config.
            var token = ctx.Request.Cookies["auth_token"];
            if (string.IsNullOrEmpty(token))
            {
                ctx.RejectPrincipal();
                return;
            }
            // Re-validate the JWT using the same parameters configured for Bearer.
            var jwtOpts  = ctx.HttpContext.RequestServices
                .GetRequiredService<IOptionsSnapshot<JwtBearerOptions>>()
                .Get(JwtBearerDefaults.AuthenticationScheme);
            var handler  = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
            try
            {
                ctx.Principal = handler.ValidateToken(
                    token,
                    jwtOpts.TokenValidationParameters,
                    out _);
                // No explicit ctx.Success() — setting Principal is sufficient;
                // omitting it suppresses the CS1061 compile error.
            }
            catch
            {
                ctx.RejectPrincipal();
            }
            await Task.CompletedTask;
        }
    };
});

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtSettings>>((options, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            // Tightened from 2 minutes — expired tokens were valid for 120s after expiry,
            // giving an attacker a wide replay window. 30s tolerates clock drift between
            // Render's nodes without meaningfully extending token lifetime.
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization(options =>
{
    // "VerifiedEmail" — applied via [Authorize(Policy = "VerifiedEmail")] on
    // high-stakes write endpoints (Upload, Checkout, Payouts, ApiKeys, Wallet)
    // so an unverified registration cannot trigger purchases or payouts.
    // The claim is set in AuthService.GenerateJwt from ApplicationUser.EmailVerified.
    options.AddPolicy("VerifiedEmail", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            ctx.User.HasClaim(c => c.Type == "email_verified" && c.Value == "true"));
    });
});
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
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "JWT Bearer token. Example: \"Authorization: Bearer {token}\"",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

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

    // Per-API-key rate limit for public V1 endpoints.
    // Falls back to IP when no key is present (anonymous access).
    options.AddPolicy("api_key_free", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Request.Headers.TryGetValue("X-API-Key", out var k) && !string.IsNullOrEmpty(k)
                ? k.ToString()
                : ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// CORS
builder.AddCorsPolicy();

// Services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ICatalogService, CatalogService>();
builder.Services.AddScoped<ILibraryService, LibraryService>();
builder.Services.AddScoped<ICheckoutService, CheckoutService>();
builder.Services.AddScoped<IPayoutService, PayoutService>();
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<IPurchaseService, PurchaseService>();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddScoped<IUploadService, UploadService>();
builder.Services.AddScoped<IWebhookService, StripeWebhookService>();
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddScoped<IWalletService, WalletService>();
builder.Services.AddScoped<IBillingService, BillingService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<IStreamService, StreamService>();
builder.Services.AddScoped<IDownloadService, DownloadService>();
builder.Services.AddScoped<ICreatorService, CreatorService>();
builder.Services.AddSingleton<IFeeService, FeeService>();
builder.Services.AddSingleton<ITierService, TierService>();
builder.Services.AddScoped<IStorefrontService, StorefrontService>();
builder.Services.AddScoped<ICreatorConnectService, CreatorConnectService>();
builder.Services.AddScoped<ILicenseService, LicenseService>();
builder.Services.AddScoped<IMarketplaceIntegrityService, Cambrian.Persistence.Services.MarketplaceIntegrityService>();
builder.Services.AddScoped<IDebugService, Cambrian.Persistence.Services.DebugService>();
builder.Services.AddScoped<IHealthService, Cambrian.Persistence.Services.HealthService>();

// Anti-drift: single source of truth services
builder.Services.AddScoped<IEntitlementService, EntitlementService>();
builder.Services.AddSingleton<ITrackVisibilityPolicy, TrackVisibilityPolicy>();

// Growth features
builder.Services.Configure<Cambrian.Infrastructure.Options.GrowthFeaturesOptions>(
    builder.Configuration.GetSection("GrowthFeatures"));
builder.Services.AddScoped<IFeatureFlagService, Cambrian.Infrastructure.FeatureFlags.ConfigurationFeatureFlagService>();
builder.Services.AddScoped<IActivityService, Cambrian.Persistence.Services.ActivityService>();
builder.Services.AddScoped<IAnalyticsService, Cambrian.Persistence.Services.AnalyticsService>();
builder.Services.AddScoped<IActivityBackfillService, Cambrian.Persistence.Services.ActivityBackfillService>();

// AI Discovery
builder.Services.AddSingleton<Cambrian.Application.AI.Discovery.Ranking.ITrackRankingService,
                              Cambrian.Application.AI.Discovery.Ranking.TrackRankingService>();
builder.Services.AddScoped<Cambrian.Application.AI.Discovery.Services.ITrackDiscoveryService,
                           Cambrian.Application.AI.Discovery.Services.TrackDiscoveryService>();

// MCP Server
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<Cambrian.Api.Mcp.CambrianMcpTools>()
    .WithResources<Cambrian.Api.Mcp.CambrianMcpResources>();

builder.Services.AddScoped<IApiKeyService, ApiKeyService>();

// Repositories
builder.Services.AddScoped<ITrackRepository, TrackRepository>();
builder.Services.AddScoped<IPurchaseRepository, PurchaseRepository>();
builder.Services.AddScoped<ILibraryRepository, LibraryRepository>();
builder.Services.AddScoped<IPayoutRepository, PayoutRepository>();
builder.Services.AddScoped<IAdminRepository, AdminRepository>();
builder.Services.AddScoped<IStreamRepository, StreamRepository>();
builder.Services.AddScoped<IWalletRepository, WalletRepository>();
builder.Services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
builder.Services.AddScoped<IInvoiceRepository, InvoiceRepository>();
builder.Services.AddScoped<ILicenseCertificateRepository, LicenseCertificateRepository>();
builder.Services.AddScoped<IAnalyticsRepository, AnalyticsRepository>();
builder.Services.AddScoped<IFeatureFlagRepository, FeatureFlagRepository>();
builder.Services.AddScoped<ICreatorProfileRepository, CreatorProfileRepository>();
builder.Services.AddScoped<ICreatorIdentityRepository, CreatorIdentityRepository>();
builder.Services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
builder.Services.AddScoped<ITransactionManager, EfTransactionManager>();

// Infrastructure
builder.Services.AddSingleton<IPaymentGateway, StripeFacade>();
builder.AddStorageProvider();
builder.AddEmailProvider();
builder.AddSmsProvider();

// Forwarded headers — Render terminates TLS at its load balancer and forwards
// requests as HTTP internally.  Without this, Request.Scheme returns "http"
// and all URLs generated by ResolveAbsoluteUrl would cause HTTP→HTTPS redirects.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Must be first — before any middleware that reads Request.Scheme or Request.Host.
app.UseForwardedHeaders();

{
    var jwt = app.Services.GetRequiredService<IOptions<JwtSettings>>().Value;
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Cambrian.Api.Jwt");
    var keyHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(jwt.Key)));

    logger.LogInformation(
        "JWT Config Loaded | Issuer: {Issuer} | Audience: {Audience} | KeyHash: {KeyHash}",
        jwt.Issuer,
        jwt.Audience,
        keyHash[..8]);
}

// Swagger JSON served in ALL environments at /swagger/v1/swagger.json
// so the frontend can codegen from it. UI only in Development.
app.UseSwagger(c =>
{
    c.RouteTemplate = "swagger/{documentName}/swagger.json";
});
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI();
}

// Canonical OpenAPI endpoint: GET /openapi.json → redirects to swagger doc
app.MapGet("/openapi.json", () => Results.Redirect("/swagger/v1/swagger.json", permanent: false))
   .ExcludeFromDescription();

// Endpoint manifest: derived from the live Swagger document
app.MapGet("/manifest.json", (Swashbuckle.AspNetCore.Swagger.ISwaggerProvider swaggerProvider) =>
{
    var doc = swaggerProvider.GetSwagger("v1");
    var endpoints = new List<object>();
    foreach (var (pathKey, pathItem) in doc.Paths)
    {
        foreach (var (method, operation) in pathItem.Operations)
        {
            var hasBearerSecurity = operation.Security?.Any(r => r.Keys.Any(k => k.Reference?.Id == "Bearer")) == true;
            var tag = operation.Tags?.FirstOrDefault()?.Name ?? "Other";
            endpoints.Add(new { method = method.ToString().ToUpperInvariant(), path = pathKey, requiresAuth = hasBearerSecurity, tag });
        }
    }
    return Results.Json(new { version = "v1", generatedAt = DateTime.UtcNow, endpoints });
}).ExcludeFromDescription();

// Normalize double-slash paths (e.g. //health → /health)
// Prevents 404s when VITE_BACKEND_API has a trailing slash.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value;
    if (path != null && path.Contains("//"))
    {
        context.Request.Path = path.Replace("//", "/");
    }
    await next();
});

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

    // Cross-Origin-Resource-Policy: emit "cross-origin" on image and audio
    // responses so clients that bypass the Vercel image proxy (production CDN
    // setups, third-party embeds, MCP/AI consumers) don't hit Opaque Response
    // Blocking. Set via OnStarting so the Content-Type is populated by the
    // time we inspect it. Also covers 302 redirect responses from /stream/*.
    context.Response.OnStarting(() =>
    {
        var contentType = context.Response.ContentType;
        var path = context.Request.Path.Value ?? string.Empty;

        var isMedia =
            (contentType is not null && (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                                      || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)))
            || path.StartsWith("/images/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/stream/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/uploads/covers/", StringComparison.OrdinalIgnoreCase);

        if (isMedia)
        {
            context.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
        }

        return Task.CompletedTask;
    });

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

// DevAuthMiddleware grants admin via "Bearer test-audit-token" for local audits.
// It MUST never be reachable from non-Development pipelines, even though the middleware
// itself short-circuits on env check — defense in depth.
if (app.Environment.IsDevelopment())
{
    app.UseMiddleware<DevAuthMiddleware>();
}

app.UseAuthentication();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapMcp();

// Alias: /sse → /mcp (307 preserves HTTP method for MCP Streamable HTTP clients)
app.MapMethods("/sse", new[] { "GET", "POST", "DELETE" }, (HttpContext ctx) =>
{
    var query = ctx.Request.QueryString.Value ?? "";
    return Results.Redirect($"/mcp{query}", permanent: false, preserveMethod: true);
});

await app.RunMigrationsAsync();
await app.SeedDataAsync();

// ── Storage probe (runs once at boot) ──
// Proves whether THIS backend process can authenticate against Supabase/S3
// using the Storage__* env vars loaded at startup. Lets Render deploy logs
// reveal credential/endpoint/region issues immediately, before any user
// hits an image or stream endpoint.
try
{
    using var probeScope = app.Services.CreateScope();
    var storage = probeScope.ServiceProvider.GetRequiredService<IObjectStorage>();
    var probe = await storage.ProbeAsync();
    if (probe.HeadBucketOk)
    {
        app.Logger.LogInformation(
            "[STORAGE-DIAG] Startup HeadBucket OK: bucket={Bucket} endpoint={Endpoint} region={Region} location={BucketLocation}",
            probe.Bucket, probe.Endpoint, probe.Region, probe.BucketLocation);
    }
    else
    {
        app.Logger.LogError(
            "[STORAGE-DIAG] Startup HeadBucket FAILED: bucket={Bucket} endpoint={Endpoint} region={Region} error={Error}",
            probe.Bucket, probe.Endpoint, probe.Region, probe.HeadBucketError);
    }
}
catch (Exception ex)
{
    // Probe itself must never crash startup — it exists to diagnose, not to gate boot.
    app.Logger.LogError(ex, "[STORAGE-DIAG] Startup probe threw unexpectedly.");
}

// ── Activity backfill (idempotent — safe on every startup) ──
try
{
    using var scope = app.Services.CreateScope();
    var backfill = scope.ServiceProvider.GetRequiredService<IActivityBackfillService>();
    await backfill.BackfillAsync(CancellationToken.None);
}
catch (Exception ex) when (ex.GetType().Name.Contains("Postgres") || ex is InvalidOperationException)
{
    app.Logger.LogWarning(ex, "Activity backfill skipped (table may not exist yet).");
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Activity backfill failed unexpectedly.");
}

await app.RunAsync();

// Expose the implicit Program class for WebApplicationFactory<Program> in integration tests
public partial class Program
{
    protected Program() { }
}
