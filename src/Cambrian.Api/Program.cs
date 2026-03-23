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
Console.WriteLine("[Startup] Governance contract version: 2.1.0 — see policy/POLICY.md, contracts/API_CONTRACTS.md");

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
builder.Services.AddScoped<ITransactionManager, EfTransactionManager>();

// Infrastructure
builder.Services.AddSingleton<IPaymentGateway, StripeFacade>();
builder.AddStorageProvider();
builder.AddEmailProvider();

var app = builder.Build();

if (app.Environment.IsDevelopment() || app.Environment.EnvironmentName == "Staging")
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

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
