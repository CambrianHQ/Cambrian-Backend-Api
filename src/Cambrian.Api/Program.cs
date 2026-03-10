using System.Text;
using System.Threading.RateLimiting;
using Cambrian.Api.Common;
using Cambrian.Api.Middleware;
using Microsoft.AspNetCore.Mvc;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Email;
using Cambrian.Infrastructure.Options;
using Cambrian.Infrastructure.Storage;
using Cambrian.Infrastructure.Stripe;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// --- TEMPORARY: generate controller stubs from OpenAPI spec ---
if (args.Contains("--generate"))
{
    OpenApiControllerGenerator.Run();
    return;
}
// --- END TEMPORARY ---

// Database — check ConnectionStrings:DefaultConnection, then DATABASE_URL (Render auto-sets this)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
    connectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
if (string.IsNullOrWhiteSpace(connectionString))
    connectionString = "***REDACTED_DEV_DB_CONNECTION***";
Console.WriteLine($"[Startup] DB connection source: {(connectionString.StartsWith("postgres") ? "URI" : "ADO.NET")}");

// Render provides postgres:// URI — convert to Npgsql ADO.NET format
if (connectionString.StartsWith("postgres://") || connectionString.StartsWith("postgresql://"))
{
    var uri = new Uri(connectionString);
    var userInfo = uri.UserInfo.Split(':');
    var port = uri.Port > 0 ? uri.Port : 5432; // Render may omit port, default to 5432
    connectionString = $"Host={uri.Host};Port={port};Database={uri.AbsolutePath.TrimStart('/')};Username={userInfo[0]};Password={userInfo[1]};SSL Mode=Require;Trust Server Certificate=true";
    Console.WriteLine($"[Startup] Parsed DB URI → Host={uri.Host}, Port={port}, DB={uri.AbsolutePath.TrimStart('/')}");
}

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
    .AddEntityFrameworkStores<CambrianDbContext>();

// --- Startup validation: require critical secrets in non-Development ---
Console.WriteLine($"[Startup] Environment: {builder.Environment.EnvironmentName}");
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? Environment.GetEnvironmentVariable("Jwt__Key")
    ?? Environment.GetEnvironmentVariable("JWT_KEY")
    ?? "";
Console.WriteLine($"[Startup] JWT key present: {!string.IsNullOrWhiteSpace(jwtKey)} (len={jwtKey.Length})");
var isNonProd = builder.Environment.IsDevelopment()
    || builder.Environment.EnvironmentName == "Staging"
    || builder.Environment.EnvironmentName == "Testing";
if (string.IsNullOrWhiteSpace(jwtKey))
{
    if (isNonProd)
        jwtKey = "***REDACTED_DEV_JWT_KEY***";
    else
        throw new InvalidOperationException("Jwt:Key must be set via environment variable or secret store.");
}
if (jwtKey.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 characters.");

var stripeKey = builder.Configuration["Stripe:SecretKey"] ?? "";
if (!string.IsNullOrWhiteSpace(stripeKey))
    Stripe.StripeConfiguration.ApiKey = stripeKey;
else if (!isNonProd)
    Console.WriteLine("[WARN] Stripe:SecretKey is not configured — payment endpoints will fail.");
else
    Console.WriteLine("[INFO] Stripe:SecretKey not set — payment endpoints will return mock responses.");

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

builder.Services.AddControllers();
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

// Rate Limiting (configurable via RateLimiting section)
var globalLimit = builder.Configuration.GetValue("RateLimiting:GlobalPermitLimit", 100);
var authLimit = builder.Configuration.GetValue("RateLimiting:AuthPermitLimit", 10);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Global fixed-window per IP
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = globalLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Strict limiter for auth endpoints (login/register)
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

// CORS - allow the Vite frontend + any configured origins (staging / production)
var corsOrigins = builder.Configuration.GetSection("App:CorsOrigins").Value?
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? Array.Empty<string>();
var frontendUrl = builder.Configuration["App:FrontendUrl"] ?? "";
var defaultOrigins = new[] { "http://localhost:5173", "http://localhost:4174", "http://127.0.0.1:4174", "http://127.0.0.1:5173" };
var allOrigins = defaultOrigins
    .Concat(corsOrigins)
    .Concat(string.IsNullOrWhiteSpace(frontendUrl) ? Array.Empty<string>() : new[] { frontendUrl })
    .Where(o => !string.IsNullOrWhiteSpace(o))
    .Distinct()
    .ToArray();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var originSet = new HashSet<string>(allOrigins, StringComparer.OrdinalIgnoreCase);

        policy.SetIsOriginAllowed(origin =>
            {
                // Exact match against configured origins
                if (originSet.Contains(origin))
                    return true;

                // Allow Vercel preview deployments only for the specific project slug
                // Configure via App:VercelProjectSlug (e.g. "cambrian-frontend")
                var vercelSlug = builder.Configuration["App:VercelProjectSlug"] ?? "";
                if (!string.IsNullOrEmpty(vercelSlug)
                    && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                    && uri.Host.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase)
                    && uri.Host.Contains(vercelSlug, StringComparison.OrdinalIgnoreCase))
                    return true;

                return false;
            })
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

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
builder.Services.AddScoped<ICreatorConnectService, CreatorConnectService>();
builder.Services.AddScoped<IMarketplaceIntegrityService, Cambrian.Persistence.Services.MarketplaceIntegrityService>();

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

// Infrastructure
builder.Services.AddSingleton<IPaymentGateway, StripeFacade>();

// Storage — choose provider based on config
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
var storageProvider = builder.Configuration["Storage:Provider"]?.ToLowerInvariant() ?? "local";
switch (storageProvider)
{
    case "s3":
    case "r2":
        builder.Services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        break;
    default:
        builder.Services.AddSingleton<IObjectStorage, LocalObjectStorage>();
        break;
}

// Email — choose provider based on config
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
var emailProvider = builder.Configuration["Email:Provider"]?.ToLowerInvariant() ?? "console";
switch (emailProvider)
{
    case "smtp":
        builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
        break;
    default:
        builder.Services.AddSingleton<IEmailService, ConsoleEmailService>();
        break;
}

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
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    await next();
});

// app.UseHttpsRedirection(); // disabled for local dev
app.UseCors();
app.UseStaticFiles(); // serve uploaded files from wwwroot (after CORS so headers apply)
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// ── Auto-migrate database in non-production environments ──
if (app.Environment.IsDevelopment() || app.Environment.EnvironmentName == "Staging")
{
    try
    {
        using var migrateScope = app.Services.CreateScope();
        var migrateDb = migrateScope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        migrateDb.Database.Migrate();
        Console.WriteLine("[Startup] Database migrations applied successfully");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Startup] Migration error: {ex.Message}");
    }
}

// ── Seed demo tracks if the table is empty ──
{
    Console.WriteLine("[Seed] Checking for tracks...");
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var hasVisible = db.Tracks.Any(t => !t.ExclusiveSold && t.Visibility == "public");
        Console.WriteLine($"[Seed] Has visible tracks: {hasVisible}");
        if (!hasVisible)
        {
            // Fix any existing tracks to be visible instead of deleting (FK constraints)
            var broken = db.Tracks.Where(t => t.ExclusiveSold || t.Visibility != "public").ToList();
            foreach (var t in broken) { t.ExclusiveSold = false; t.Visibility = "public"; }
            if (broken.Any()) { db.SaveChanges(); Console.WriteLine($"[Seed] Fixed {broken.Count} existing tracks"); }

            var um = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
            var creator = um.Users.FirstOrDefault();
            var creatorId = creator?.Id;
            Console.WriteLine($"[Seed] Creator: {creatorId} ({creator?.Email})");
            if (creatorId != null)
            {
                var tracks = new List<Track>
            {
                new() { Id = Guid.NewGuid(), Title = "Midnight Drive",       Genre = "Lo-Fi",       Price = 29, Duration = "3:42", CreatorId = creatorId, AudioUrl = "/audio/demo1.mp3", NonExclusivePriceCents = 2900, ExclusivePriceCents = 49900, Description = "Chill late-night vibes" },
                new() { Id = Guid.NewGuid(), Title = "Neon Skyline",         Genre = "Electronic",  Price = 39, Duration = "4:15", CreatorId = creatorId, AudioUrl = "/audio/demo2.mp3", NonExclusivePriceCents = 3900, ExclusivePriceCents = 59900, Description = "Synthwave-inspired beat" },
                new() { Id = Guid.NewGuid(), Title = "Golden Hour",          Genre = "R&B",         Price = 34, Duration = "3:58", CreatorId = creatorId, AudioUrl = "/audio/demo3.mp3", NonExclusivePriceCents = 3400, ExclusivePriceCents = 54900, Description = "Smooth R&B groove" },
                new() { Id = Guid.NewGuid(), Title = "Concrete Jungle",      Genre = "Hip-Hop",     Price = 49, Duration = "3:20", CreatorId = creatorId, AudioUrl = "/audio/demo4.mp3", NonExclusivePriceCents = 4900, ExclusivePriceCents = 74900, Description = "Hard-hitting trap beat" },
                new() { Id = Guid.NewGuid(), Title = "Summer Breeze",        Genre = "Pop",         Price = 24, Duration = "3:35", CreatorId = creatorId, AudioUrl = "/audio/demo5.mp3", NonExclusivePriceCents = 2400, ExclusivePriceCents = 39900, Description = "Upbeat pop instrumental" },
                new() { Id = Guid.NewGuid(), Title = "Rainy Afternoon",      Genre = "Jazz",        Price = 19, Duration = "5:10", CreatorId = creatorId, AudioUrl = "/audio/demo6.mp3", NonExclusivePriceCents = 1900, ExclusivePriceCents = 29900, Description = "Smooth jazz session" },
                new() { Id = Guid.NewGuid(), Title = "Electric Dreams",      Genre = "Electronic",  Price = 44, Duration = "4:45", CreatorId = creatorId, AudioUrl = "/audio/demo7.mp3", NonExclusivePriceCents = 4400, ExclusivePriceCents = 69900, Description = "Future bass production" },
                new() { Id = Guid.NewGuid(), Title = "Starlight",            Genre = "Indie",       Price = 29, Duration = "4:02", CreatorId = creatorId, AudioUrl = "/audio/demo8.mp3", NonExclusivePriceCents = 2900, ExclusivePriceCents = 44900, Description = "Dreamy indie beat" },
                new() { Id = Guid.NewGuid(), Title = "Urban Echoes",         Genre = "Hip-Hop",     Price = 54, Duration = "3:15", CreatorId = creatorId, AudioUrl = "/audio/demo9.mp3", NonExclusivePriceCents = 5400, ExclusivePriceCents = 84900, Description = "Atmospheric boom-bap" },
                new() { Id = Guid.NewGuid(), Title = "Velvet Sunset",        Genre = "Lo-Fi",       Price = 22, Duration = "3:50", CreatorId = creatorId, AudioUrl = "/audio/demo10.mp3", NonExclusivePriceCents = 2200, ExclusivePriceCents = 34900, Description = "Warm lo-fi textures" },
            };
            db.Tracks.AddRange(tracks);
            db.SaveChanges();
            Console.WriteLine($"[Seed] Created {tracks.Count} demo tracks for creator {creatorId}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Seed] ERROR: {ex.Message}");
    }
}

app.Run();

// Expose the implicit Program class for WebApplicationFactory<Program> in integration tests
public partial class Program { }