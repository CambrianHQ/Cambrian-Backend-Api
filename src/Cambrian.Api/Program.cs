using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Cambrian.Api;
using Cambrian.Api.Common;
using Cambrian.Api.E2e;
using Cambrian.Api.Middleware;
using Cambrian.Api.Security;
using Cambrian.Api.Services;
using Cambrian.Application.Configuration;
using Cambrian.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Cambrian.Application.Auth;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Stripe;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;
using OpenTelemetry.Metrics;
using QuestPDF.Infrastructure;
using Sentry.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
QuestPDF.Settings.License = LicenseType.Community;

// --- TEMPORARY: generate controller stubs from OpenAPI spec ---
if (args.Contains("--generate"))
{
    OpenApiControllerGenerator.Run();
    return;
}
// --- END TEMPORARY ---

// Error monitoring. Newer Sentry SDK versions reject a null DSN while the host is
// being constructed, which also breaks design-time tooling such as `dotnet swagger`.
// Register Sentry only when a real DSN is configured; an empty DSN is an intentional
// disabled state for tests, local tooling, and environments that do not use Sentry.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Sentry:Dsn"]))
{
    builder.WebHost.UseSentry();
}

// CLI: `dotnet run -- --seed` — run migrations + seed, then exit.
// Useful for CI/CD pipelines and manual staging environment setup.
if (args.Contains("--seed"))
{
    var seedApp = builder.Build();
    await seedApp.RunMigrationsAsync();
    await seedApp.SeedDataAsync();
    Console.WriteLine("[CLI] Seed complete — exiting.");
    return;
}

const string TestingEnvironment = "Testing";

// Windows' default host logging includes Event Log, which requires privileges
// unavailable to ordinary developer/test processes. A harmless startup warning
// (for example, Swagger running without a web root) must not crash tooling.
if (builder.Environment.IsEnvironment(TestingEnvironment) && OperatingSystem.IsWindows())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
}

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
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
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
const string CookieSchemeName = "CookieJwt";

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
.AddJwtBearer(CookieSchemeName, _ => { });

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtSettings>>((options, jwtOptions) =>
    {
        JwtAuthenticationConfiguration.Configure(
            options,
            jwtOptions.Value,
            AuthenticationConstants.AuthTransportBearer);
    });

builder.Services.AddOptions<JwtBearerOptions>(CookieSchemeName)
    .Configure<IOptions<JwtSettings>>((options, jwtOptions) =>
    {
        JwtAuthenticationConfiguration.Configure(
            options,
            jwtOptions.Value,
            AuthenticationConstants.AuthTransportCookie,
            "auth_token");
    });

builder.Services.AddAuthorization(options =>
{
    var interactiveUserPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => Program.IsInteractiveUser(ctx.User))
        .Build();

    // Generic [Authorize] is deliberately interactive-user-only. API keys must
    // opt into the ApiKeyIntegration policy and AllowApiKey endpoint metadata.
    options.DefaultPolicy = interactiveUserPolicy;
    options.AddPolicy(AuthenticationConstants.InteractiveUserPolicy, interactiveUserPolicy);
    options.AddPolicy(AuthenticationConstants.ApiKeyIntegrationPolicy, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(
            AuthenticationConstants.AuthMethodClaim,
            AuthenticationConstants.AuthMethodApiKey);
    });

    // "VerifiedEmail" — applied via [Authorize(Policy = "VerifiedEmail")] on
    // high-stakes write endpoints (Upload, Checkout, Payouts, ApiKeys, Wallet)
    // so an unverified registration cannot trigger purchases or payouts.
    // The claim is set in AuthService.GenerateJwt from ApplicationUser.EmailVerified.
    options.AddPolicy("VerifiedEmail", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => Program.IsInteractiveUser(ctx.User));
        policy.RequireAssertion(ctx =>
            ctx.User.HasClaim(c => c.Type == "email_verified" && c.Value == "true"));
    });

    // Capability-based policies — read from HttpContext.Items["Capabilities"]
    // With endpoint routing, AuthorizationHandlerContext.Resource is the HttpContext.
    options.AddPolicy("CanUploadTrack", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => Program.IsInteractiveUser(ctx.User));
        policy.RequireAssertion(ctx =>
            HasCapability(ctx.Resource as HttpContext, Cambrian.Domain.Auth.Capabilities.TrackUpload));
    });
    options.AddPolicy("CanEditOwnTrack", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => Program.IsInteractiveUser(ctx.User));
        policy.RequireAssertion(ctx =>
            HasCapability(ctx.Resource as HttpContext, Cambrian.Domain.Auth.Capabilities.TrackEditOwn));
    });
    options.AddPolicy("CanDeleteOwnTrack", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => Program.IsInteractiveUser(ctx.User));
        policy.RequireAssertion(ctx =>
            HasCapability(ctx.Resource as HttpContext, Cambrian.Domain.Auth.Capabilities.TrackDeleteOwn));
    });
    options.AddPolicy("CanRequestPayout", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => Program.IsInteractiveUser(ctx.User));
        policy.RequireAssertion(ctx =>
            HasCapability(ctx.Resource as HttpContext, Cambrian.Domain.Auth.Capabilities.PayoutRequest));
    });
    options.AddPolicy("CanPurchaseLicense", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => Program.IsInteractiveUser(ctx.User));
        policy.RequireAssertion(ctx =>
            HasCapability(ctx.Resource as HttpContext, Cambrian.Domain.Auth.Capabilities.LicensePurchase));
    });
});
builder.Services.AddMemoryCache();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<CreatorImageUploadGrantService>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "cambrian_csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// OpenTelemetry metrics → Prometheus scraping endpoint at GET /metrics.
//  - AspNetCore/HttpClient instrumentation feed the http_server_* / http_client_*
//    histograms the Grafana dashboards query (grafana/dashboards/).
//  - Runtime instrumentation supplies process/GC gauges for the executive dashboard.
//  - AddMeter("Cambrian") subscribes to the custom business counters defined in
//    Cambrian.Application.Observability.CambrianMetrics.
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(Cambrian.Application.Observability.CambrianMetrics.MeterName)
        .AddPrometheusExporter());

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

    // Every rejection (global limiter or a named policy) lands here exactly once.
    // The default rejection writes only the status code — no body — which frontend
    // callers see as an empty/undefined response. Always return retryAfter in
    // seconds (rounded up) so callers can back off intelligently.
    options.OnRejected = async (ctx, cancellationToken) =>
    {
        var retryAfterSeconds = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? (int)Math.Ceiling(retryAfter.TotalSeconds)
            : 60;
        ctx.HttpContext.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
        ctx.HttpContext.Response.ContentType = "application/json";
        await ctx.HttpContext.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = "rate_limited",
            message = "Too many requests. Please slow down and try again shortly.",
            retryAfter = retryAfterSeconds,
        }), cancellationToken);
    };

    // Partitioned by authenticated user id (falling back to connection address for
    // anonymous requests) — see ClientRateLimitKey.FromUserOrConnection for why an
    // IP-only key isn't safe behind a reverse proxy.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ClientRateLimitKey.FromUserOrConnection(ctx),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = globalLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.AddPolicy("auth", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ClientRateLimitKey.FromUserOrConnection(ctx),
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
                : ClientRateLimitKey.FromConnection(ctx),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Community boosts: ~30 actions/min per authenticated user (falls back to IP).
    options.AddPolicy("community", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? ClientRateLimitKey.FromConnection(ctx),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    options.AddPolicy("mcp", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? ClientRateLimitKey.FromConnection(ctx),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Creator profile editing (CreatorProfileController): get/put profile, banner/avatar
    // upload, collections CRUD, follow/unfollow, public profile view. Previously shared
    // the "auth" policy (10/min) meant for login/register brute-force protection — a
    // single normal edit session (load profile, upload a banner, upload an avatar, save,
    // list collections) already costs 6-8 requests, so a second image re-upload or a
    // quick follow-up save routinely 429'd. This mirrors the "community"/"mcp" pattern
    // at a limit sized for a real editing session instead of a sensitive auth endpoint.
    options.AddPolicy("creatorProfile", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? ClientRateLimitKey.FromConnection(ctx),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// CORS
builder.AddCorsPolicy();

// Services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ICatalogService, CatalogService>();
// Public, read-only discovery surface for the external MCP server / SEO / AI crawlers.
builder.Services.AddSingleton<IPublicUrlResolver, PublicUrlResolver>();
builder.Services.AddScoped<IPublicApiService, PublicApiService>();
builder.Services.AddScoped<ILibraryService, LibraryService>();
builder.Services.AddScoped<IPayoutService, PayoutService>();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddScoped<IUploadService, UploadService>();
builder.Services.AddScoped<IWebhookService, StripeWebhookService>();
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddScoped<IWalletService, WalletService>();
builder.Services.AddScoped<ITrackBoostService, TrackBoostService>();
builder.Services.AddScoped<IBillingService, BillingService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<IStreamService, StreamService>();
builder.Services.AddScoped<IDownloadService, DownloadService>();
builder.Services.AddScoped<ICreatorService, CreatorService>();
builder.Services.AddSingleton<IFeeService, FeeService>();
builder.Services.AddSingleton<ITierService, TierService>();
// Weekly "The Scene" charts — reads are served from the persisted
// WeeklyChartSnapshots table (no in-process cache); recompute is scheduled
// (WeeklyChartWorker, below) and also admin-triggerable on demand.
builder.Services.AddSingleton<IWeeklyChartService, WeeklyChartService>();
builder.Services.AddScoped<IWeeklyChartRepository, WeeklyChartRepository>();
// Weekly creator digest (creator-audit fix 10). The worker is double-gated:
// Digest:Enabled (default false) AND not Testing. DryRun defaults true, so
// even an enabled deploy logs recipients instead of emailing until DryRun is
// explicitly flipped off.
builder.Services.AddScoped<IWeeklyDigestRepository, WeeklyDigestRepository>();
builder.Services.AddScoped<ICreatorMilestoneRepository, CreatorMilestoneRepository>();
builder.Services.AddSingleton<IWeeklyDigestService, WeeklyDigestService>();
if (builder.Configuration.GetValue("Digest:Enabled", false)
    && builder.Environment.EnvironmentName != TestingEnvironment)
{
    builder.Services.AddHostedService<Cambrian.Api.BackgroundServices.WeeklyDigestWorker>();
}
// Scheduled weekly-chart recompute (idempotent per week; admin POST stays as a
// manual trigger). Not in Testing for the same reason as MasteringWorker: the
// periodic DB touch would share the test host's single SQLite connection.
if (builder.Environment.EnvironmentName != TestingEnvironment)
    builder.Services.AddHostedService<Cambrian.Api.BackgroundServices.WeeklyChartWorker>();
builder.Services.AddScoped<IStorefrontService, StorefrontService>();
builder.Services.AddScoped<ICreatorConnectService, CreatorConnectService>();

// Public v1 API supporting services
builder.Services.AddScoped<Cambrian.Application.Interfaces.V1.IIdempotencyStore,
    Cambrian.Persistence.Repositories.IdempotencyStore>();
builder.Services.AddScoped<Cambrian.Api.Middleware.ApiUsageActionFilter>();
builder.Services.AddScoped<IMarketplaceIntegrityService, Cambrian.Persistence.Services.MarketplaceIntegrityService>();
builder.Services.AddScoped<IDebugService, Cambrian.Persistence.Services.DebugService>();
builder.Services.AddScoped<IHealthService, Cambrian.Persistence.Services.HealthService>();
builder.Services.AddScoped<IPreflightService, Cambrian.Infrastructure.Diagnostics.PreflightService>();
builder.Services.AddSingleton<ILocalDeliveryDebugStore, LocalDeliveryDebugStore>();

// Capability-based authorization
builder.Services.AddScoped<ICapabilityResolver, CapabilityResolver>();

// Anti-drift: single source of truth services
builder.Services.AddScoped<IEntitlementService, EntitlementService>();
builder.Services.AddScoped<IPlanEntitlementService, PlanEntitlementService>();
builder.Services.AddSingleton<ITrackVisibilityPolicy, TrackVisibilityPolicy>();

// §9 provenance / authorship / compliance
// Production must supply a stable signing key so stamps verify across restarts.
if (builder.Environment.IsProduction()
    && string.IsNullOrWhiteSpace(builder.Configuration["Provenance:Signing:PrivateKeyPem"]))
{
    throw new InvalidOperationException(
        "Provenance:Signing:PrivateKeyPem must be configured in Production — provenance stamps must "
        + "verify across restarts. Generate an EC P-256 key and supply it as PKCS#8 PEM.");
}
builder.Services.AddSingleton<IProvenanceSigner, Cambrian.Infrastructure.Provenance.EcdsaProvenanceSigner>();

// Batched on-chain anchoring (job + provider). Provider selects the IProvenanceAnchor; the
// worker only runs when explicitly enabled, so tests/dev never anchor by surprise.
builder.Services.Configure<ProvenanceAnchorOptions>(
    builder.Configuration.GetSection(ProvenanceAnchorOptions.SectionName));
var anchorOptions = builder.Configuration.GetSection(ProvenanceAnchorOptions.SectionName)
    .Get<ProvenanceAnchorOptions>() ?? new ProvenanceAnchorOptions();

if (string.Equals(anchorOptions.Provider, "evm", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(anchorOptions.RpcUrl) || string.IsNullOrWhiteSpace(anchorOptions.PrivateKey))
        throw new InvalidOperationException(
            "Provenance:Anchor:Provider=evm requires Provenance:Anchor:RpcUrl and Provenance:Anchor:PrivateKey.");
    builder.Services.AddSingleton<IProvenanceAnchor, Cambrian.Infrastructure.Provenance.EvmMerkleProvenanceAnchor>();
}
else
{
    builder.Services.AddSingleton<IProvenanceAnchor, Cambrian.Infrastructure.Provenance.NoOpProvenanceAnchor>();
}

builder.Services.AddScoped<ProvenanceAnchorBatchProcessor>();
if (anchorOptions.JobEnabled)
    builder.Services.AddHostedService<Cambrian.Api.BackgroundServices.ProvenanceAnchorBatchService>();

builder.Services.AddScoped<IProvenanceService, ProvenanceService>();
builder.Services.AddScoped<IAuthorshipService, AuthorshipService>();
builder.Services.AddScoped<IComplianceScoreService, ComplianceScoreService>();
builder.Services.AddScoped<ITrackLegitimacyService, TrackLegitimacyService>();

// ── Release Ready mastering (config-switched engine) ──
// FfmpegEngine is the default so the pipeline never blocks on RoEx approval;
// flip Mastering:Engine to "tonn" once the RoEx key is provisioned.
builder.Services.Configure<MasteringOptions>(builder.Configuration.GetSection(MasteringOptions.SectionName));
var masteringOptions = builder.Configuration.GetSection(MasteringOptions.SectionName)
    .Get<MasteringOptions>() ?? new MasteringOptions();

if (string.Equals(masteringOptions.Engine, "tonn", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<IMasteringEngine, Cambrian.Infrastructure.Mastering.TonnEngine>(c =>
        c.Timeout = TimeSpan.FromSeconds(120));
}
else
{
    builder.Services.AddScoped<IMasteringEngine, Cambrian.Infrastructure.Mastering.FfmpegEngine>();
}

builder.Services.AddSingleton<IReleaseValidationService, Cambrian.Infrastructure.Validation.ReleaseValidationService>();
// Injected clock so credit month-boundary logic is deterministically testable.
builder.Services.AddSingleton(TimeProvider.System);

// Release Ready credits, persistence, orchestration + the in-process mastering worker.
builder.Services.AddScoped<IMasteringJobRepository, MasteringJobRepository>();
builder.Services.AddScoped<IReleaseCreditPurchaseRepository, ReleaseCreditPurchaseRepository>();
builder.Services.AddScoped<IReleaseCreditService, ReleaseCreditService>();
builder.Services.AddScoped<IReleaseReadyService, ReleaseReadyService>();
// Not in Testing: the worker's 3-second DB poll shares the test host's single
// in-memory SQLite connection and intermittently collides with test requests
// (random 500s in unrelated tests, e.g. the SlugConflict flake). Pipeline tests
// that need the worker re-add it explicitly (ReleasePipelineFixture).
if (builder.Environment.EnvironmentName != TestingEnvironment)
    builder.Services.AddHostedService<Cambrian.Api.BackgroundServices.MasteringWorker>();

// Release pipeline: readiness scoring + track-based release-ready jobs.
builder.Services.AddSingleton<ITrackReadinessCache, Cambrian.Api.Services.MemoryTrackReadinessCache>();
builder.Services.AddScoped<ITrackReadinessService, TrackReadinessService>();
builder.Services.AddScoped<ITrackReleasePipelineService, TrackReleasePipelineService>();

// Paid authorship records (issued by the Stripe webhook after payment).
builder.Services.AddScoped<IAuthorshipRecordRepository, AuthorshipRecordRepository>();
builder.Services.AddScoped<IAuthorshipRecordService, AuthorshipRecordService>();
builder.Services.AddScoped<IAuthorshipRecordIssuer>(sp => sp.GetRequiredService<IAuthorshipRecordService>());
builder.Services.AddScoped<IAuthorshipCertificatePdfService, AuthorshipCertificatePdfService>();

// Connect money-in: tips + fan subscriptions on artists' connected accounts.
builder.Services.AddScoped<IFanSubscriptionRepository, FanSubscriptionRepository>();
builder.Services.AddScoped<IEarningsRepository, EarningsRepository>();
builder.Services.AddScoped<IArtistMonetizationService, ArtistMonetizationService>();
builder.Services.AddScoped<IConnectWebhookService, Cambrian.Infrastructure.Stripe.StripeConnectWebhookService>();

// Growth features
builder.Services.Configure<Cambrian.Infrastructure.Options.GrowthFeaturesOptions>(
    builder.Configuration.GetSection("GrowthFeatures"));
builder.Services.AddHttpClient<IPurchaseAnalyticsService, Cambrian.Infrastructure.Analytics.PostHogPurchaseAnalyticsService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(2);
});
builder.Services.AddScoped<IFeatureFlagService, Cambrian.Infrastructure.FeatureFlags.ConfigurationFeatureFlagService>();
builder.Services.AddScoped<IActivityService, Cambrian.Persistence.Services.ActivityService>();
builder.Services.AddScoped<IAnalyticsService, Cambrian.Persistence.Services.AnalyticsService>();
builder.Services.AddScoped<IActivityBackfillService, Cambrian.Persistence.Services.ActivityBackfillService>();
builder.Services.AddScoped<INewsletterService, Cambrian.Persistence.Services.NewsletterService>();

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
builder.Services.AddScoped<ITrackBoostRepository, TrackBoostRepository>();
builder.Services.AddScoped<IPayoutRepository, PayoutRepository>();
builder.Services.AddScoped<IAdminRepository, AdminRepository>();
builder.Services.AddScoped<IStreamRepository, StreamRepository>();
builder.Services.AddScoped<IWalletRepository, WalletRepository>();
builder.Services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
// Subscription expiry sweep (keeps Status truthful; tier enforcement is at read
// time in GetActiveAsync). Not in Testing — same reason as the other workers.
if (builder.Environment.EnvironmentName != TestingEnvironment)
    builder.Services.AddHostedService<Cambrian.Api.BackgroundServices.SubscriptionExpiryWorker>();
builder.Services.AddScoped<IInvoiceRepository, InvoiceRepository>();
builder.Services.AddScoped<IAnalyticsRepository, AnalyticsRepository>();
builder.Services.AddScoped<IFeatureFlagRepository, FeatureFlagRepository>();
builder.Services.AddScoped<ICreatorProfileRepository, CreatorProfileRepository>();
builder.Services.AddScoped<ITrackDetailsRepository, TrackDetailsRepository>();
builder.Services.AddScoped<IPublicDirectoryRepository, PublicDirectoryRepository>();
builder.Services.AddScoped<ICreatorIdentityRepository, CreatorIdentityRepository>();
builder.Services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
builder.Services.AddScoped<IEntitlementRepository, EntitlementRepository>();
builder.Services.AddScoped<IProvenanceAnchorRepository, ProvenanceAnchorRepository>();
builder.Services.AddScoped<ITrackAuthorshipRepository, TrackAuthorshipRepository>();
builder.Services.AddScoped<ITransactionManager, EfTransactionManager>();

// Infrastructure
builder.AddPaymentGateway();
builder.AddStorageProvider();
builder.AddEmailProvider();
builder.AddSmsProvider();

// Forwarded headers are enabled only when the deployment supplies an explicit
// trusted proxy/network allow-list. An empty allow-list must never mean
// "trust every direct client".
var trustedForwardersConfigured =
    ForwardedHeaderConfiguration.HasTrustedForwarders(builder.Configuration);
if (trustedForwardersConfigured)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
        ForwardedHeaderConfiguration.Configure(options, builder.Configuration));
}

// Test-only E2E support service — registered ONLY when the surface is enabled (Testing, or
// Development with Cambrian:E2E:Enabled=true). Never present in Production/Staging.
if (E2eSupport.IsEnabled(builder.Environment, builder.Configuration))
{
    builder.Services.AddScoped<E2eScenarioService>();
}

var app = builder.Build();

// Must be first — before any middleware that reads Request.Scheme or Request.Host.
if (trustedForwardersConfigured)
{
    app.UseForwardedHeaders();
}
else if (app.Environment.IsProduction())
{
    // Render's public service is HTTPS-only. Force the canonical scheme without
    // trusting spoofable X-Forwarded-* values from direct-origin clients.
    app.Use((context, next) =>
    {
        context.Request.Scheme = Uri.UriSchemeHttps;
        return next();
    });
    app.Logger.LogWarning(
        "Forwarded headers disabled: configure ForwardedHeaders:KnownProxies or KnownNetworks before relying on client IP forwarding.");
}

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

// Endpoint manifest: derived from ASP.NET endpoint metadata, not Swagger security.
// Swagger can describe contracts, but authorization truth lives on endpoint metadata.
app.MapGet("/manifest.json", (IEnumerable<EndpointDataSource> endpointDataSources) =>
    Results.Json(EndpointManifestFactory.Build(endpointDataSources, DateTimeOffset.UtcNow)))
    .ExcludeFromDescription();

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

// Serve static files — block direct access to uploaded audio, but keep public
// creator images (covers, avatars, banners) reachable. See StaticUploadPolicy.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (Cambrian.Api.Infrastructure.StaticUploadPolicy.ShouldBlock(ctx.File.PhysicalPath))
        {
            ctx.Context.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Context.Response.ContentLength = 0;
            ctx.Context.Response.Body = Stream.Null;
        }
    }
});

app.UseRateLimiter();
app.UseMiddleware<VerifiedEmailForbiddenResponseMiddleware>();

// Bound MCP request bodies even when Content-Length is omitted/chunked.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp")
        || context.Request.Path.StartsWithSegments("/sse"))
    {
        const long maxMcpBodySize = 1024 * 1024;
        var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = maxMcpBodySize;

        if (context.Request.ContentLength is > maxMcpBodySize)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
    }

    await next();
});

// DevAuthMiddleware grants admin via "Bearer test-audit-token" for local audits.
// It MUST never be reachable from non-Development pipelines, even though the middleware
// itself short-circuits on env check — defense in depth.
if (app.Environment.IsDevelopment())
{
    app.UseMiddleware<DevAuthMiddleware>();
}

app.UseAuthentication();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseMiddleware<CookieCsrfProtectionMiddleware>();
app.UseMiddleware<Cambrian.Api.Middleware.CapabilityMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapMcp("/mcp")
    .WithMetadata(new AllowApiKeyAttribute())
    .RequireAuthorization(AuthenticationConstants.ApiKeyIntegrationPolicy)
    .RequireRateLimiting("mcp");

// Prometheus scrape target: GET /metrics. Anonymous and outside the OpenAPI contract
// (like /sse and /mcp) so it is internally scrapable without a token. In production this
// path should be network-restricted rather than exposed publicly.
app.MapPrometheusScrapingEndpoint();

// Alias: /sse → /mcp (307 preserves HTTP method for MCP Streamable HTTP clients)
app.MapMethods("/sse", new[] { "GET", "POST", "DELETE" }, (HttpContext ctx) =>
{
    var query = ctx.Request.QueryString.Value ?? "";
    return Results.Redirect($"/mcp{query}", permanent: false, preserveMethod: true);
})
    .WithMetadata(new AllowApiKeyAttribute())
    .RequireAuthorization(AuthenticationConstants.ApiKeyIntegrationPolicy)
    .RequireRateLimiting("mcp")
    .ExcludeFromDescription();

// Alias: /charts/weekly → same payload as /api/charts/weekly. Kept out of the
// OpenAPI contract (the canonical /api path is the documented one). (residue R17)
app.MapGet("/charts/weekly", async (IWeeklyChartService charts, CancellationToken ct) =>
{
    var chart = await charts.GetCurrentAsync(ct);
    return Results.Json(new { success = true, data = chart, message = (string?)null, error = (string?)null });
}).ExcludeFromDescription();

// One-click weekly-digest unsubscribe (linked from the digest email).
// HMAC-validated, no login, idempotent, and deliberately generic in both
// directions: an invalid token and an unknown user get the same responses,
// so the endpoint can't be used to probe which user ids exist.
app.MapGet("/email/unsubscribe", async (string? uid, string? token, IWeeklyDigestRepository digestRepo, IConfiguration config, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(token))
        return Results.BadRequest(new { success = false, error = "Invalid unsubscribe link." });

    var expected = Cambrian.Application.Services.WeeklyDigestService.ComputeUnsubscribeToken(uid, config["Jwt:Key"] ?? string.Empty);
    if (!CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(token.ToLowerInvariant())))
        return Results.BadRequest(new { success = false, error = "Invalid unsubscribe link." });

    await digestRepo.SetDigestOptOutAsync(uid, true, ct);
    return Results.Content(
        "<html><body style=\"font-family:Arial;background:#000;color:#fff;text-align:center;padding-top:80px\">" +
        "<h2>You're unsubscribed.</h2><p>You won't receive the weekly creator digest anymore.</p>" +
        "<p><a href=\"https://cambrianmusic.com/studio\" style=\"color:#00c896\">Back to your Studio</a></p></body></html>",
        "text/html");
}).ExcludeFromDescription();

// Admin trigger for the digest — supports ?dryRun=true for a safe test pass.
app.MapPost("/admin/digest/run", async (bool? dryRun, IWeeklyDigestService digest, CancellationToken ct) =>
{
    var result = await digest.RunAsync(dryRun ?? true, ct);
    return Results.Json(new { success = true, data = result, message = (string?)null, error = (string?)null });
})
    .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { Roles = "Admin" })
    .ExcludeFromDescription();

// Legacy readiness path → canonical /api/tracks/{id}/readiness (residue F7).
// The readiness endpoint has only ever lived under /api; this 308 makes the old
// un-prefixed path explicit for any stale client instead of a bare 404.
app.MapMethods("/tracks/{id}/readiness", new[] { "GET" }, (string id) =>
    Results.Redirect($"/api/tracks/{id}/readiness", permanent: true, preserveMethod: true))
    .ExcludeFromDescription();

// Lightweight keep-warm / liveness probe with build info. Anonymous, always 200,
// no DB hit — safe as an uptime-monitor / Render keep-warm target. (residue #5)
app.MapGet("/healthz", () =>
{
    var asm = typeof(Program).Assembly.GetName();
    return Results.Json(new
    {
        status = "ok",
        service = "cambrian-api",
        environment = app.Environment.EnvironmentName,
        build = new
        {
            version = asm.Version?.ToString() ?? "unknown",
            commit = Environment.GetEnvironmentVariable("GIT_COMMIT")
                ?? Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT")
                ?? "unknown",
        },
    });
}).ExcludeFromDescription();

// Commissions / collabs are not implemented yet. Expose an explicit, authenticated
// 501 so clients get a clear "not implemented" signal; the frontend also reads
// commissionsEnabled=false from /api/me/entitlements to hide the paid CTA pre-launch.
// Kept out of the OpenAPI contract (like /healthz, /charts/weekly) until it ships.
app.MapPost("/commissions", () =>
    Results.Json(
        new { success = false, data = (object?)null, message = (string?)null, error = "commissions_not_implemented" },
        statusCode: 501))
    .RequireAuthorization()
    .ExcludeFromDescription();

// Test-only E2E control plane (reset/seed/state/stripe-simulation). Mapped ONLY in Testing or
// explicitly-enabled Development — NEVER Production/Staging — and kept out of the OpenAPI
// contract. Authenticated per-request by a constant-time-compared x-e2e-secret header.
if (E2eSupport.IsEnabled(app.Environment, app.Configuration))
{
    app.MapE2e();
}

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

// ── Activity backfill (idempotent — safe on every runtime startup) ──
// Testing hosts are also used by WebApplicationFactory and `dotnet swagger`.
// They replace or intentionally omit the runtime database, so startup must stay
// free of database side effects until a test request or fixture initializes it.
if (!app.Environment.IsEnvironment(TestingEnvironment))
{
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
}

await app.RunAsync();

// Expose the implicit Program class for WebApplicationFactory<Program> in integration tests
public partial class Program
{
    protected Program() { }

    /// <summary>
    /// Check if the HttpContext has a given capability loaded by CapabilityMiddleware.
    /// </summary>
    internal static bool HasCapability(HttpContext? httpContext, string capability)
    {
        if (httpContext is null) return false;
        if (httpContext.Items.TryGetValue("Capabilities", out var caps) && caps is IReadOnlyList<string> list)
            return list.Contains(capability);
        return false;
    }

    internal static bool IsInteractiveUser(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && !user.HasClaim(
            AuthenticationConstants.AuthMethodClaim,
            AuthenticationConstants.AuthMethodApiKey);
}
