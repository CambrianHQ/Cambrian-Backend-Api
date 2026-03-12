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
{
    if (builder.Environment.IsDevelopment()
        || builder.Environment.EnvironmentName == "Testing")
        connectionString = "***REDACTED_DEV_DB_CONNECTION***";
    else
        throw new InvalidOperationException(
            "Database connection string must be set via ConnectionStrings:DefaultConnection or DATABASE_URL.");
}
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

// Store the resolved key so downstream services (AuthService) can read it from config
// without their own hardcoded fallback.
builder.Configuration["Jwt:Key"] = jwtKey;

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

builder.Services.AddMemoryCache();

builder.Services.AddControllers();

// Raise the multipart form body limit for audio uploads (default ≈128 MB,
// but Kestrel's MaxRequestBodySize is only 30 MB — raise it too).
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 150 * 1024 * 1024; // 150 MB
    o.ValueLengthLimit        = 150 * 1024 * 1024; // 150 MB (default is only 4 MB!)
    o.ValueCountLimit         = 20;                 // generous for upload form fields
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
// Only include localhost origins during development
var defaultOrigins = builder.Environment.IsDevelopment()
    ? new[] { "http://localhost:5173", "http://localhost:5174", "http://localhost:4174", "http://127.0.0.1:4174", "http://127.0.0.1:5173", "http://127.0.0.1:5174" }
    : Array.Empty<string>();
var allOrigins = defaultOrigins
    .Concat(corsOrigins)
    .Concat(string.IsNullOrWhiteSpace(frontendUrl) ? Array.Empty<string>() : new[] { frontendUrl })
    .Where(o => !string.IsNullOrWhiteSpace(o))
    .Distinct()
    .ToArray();
Console.WriteLine($"[Startup] CORS origins: {string.Join(", ", allOrigins)}");
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
builder.Services.AddScoped<ILicenseService, LicenseService>();
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
builder.Services.AddScoped<ILicenseCertificateRepository, LicenseCertificateRepository>();

// Infrastructure
builder.Services.AddSingleton<IPaymentGateway, StripeFacade>();

// Storage — choose provider based on config
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
var storageProvider = builder.Configuration["Storage:Provider"]?.ToLowerInvariant() ?? "local";
Console.WriteLine($"[Startup] Storage provider: {storageProvider}");
switch (storageProvider)
{
    case "s3":
    case "r2":
        // Validate credentials at startup — fail fast if misconfigured
        var endpoint = builder.Configuration["Storage:Endpoint"] ?? "";
        var bucket   = builder.Configuration["Storage:Bucket"] ?? "";
        var accessKey = builder.Configuration["Storage:AccessKey"] ?? "";
        var secretKey = builder.Configuration["Storage:SecretKey"] ?? "";
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(bucket)
            || string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException(
                $"Storage provider '{storageProvider}' requires Storage:Endpoint, Storage:Bucket, "
                + "Storage:AccessKey, and Storage:SecretKey to be configured.");
        }
        Console.WriteLine($"[Startup] S3 endpoint={endpoint}, bucket={bucket}");
        builder.Services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        break;
    default:
        if (!isNonProd)
            Console.WriteLine("[WARN] Using local storage in non-development environment. "
                + "Uploaded files will be lost on container restart. Set Storage:Provider=r2 for persistence.");
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
    context.Response.Headers["X-XSS-Protection"] = "0"; // deprecated — rely on CSP instead
    if (!app.Environment.IsDevelopment())
        context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    await next();
});

// HTTPS handled by Render/Vercel load balancers - no redirect needed from app
// app.UseHttpsRedirection();
app.UseCors();

// Serve static files — block direct access to uploaded audio (tracks/)
// but allow public access to cover art images (covers/).
// Audio must go through authenticated StreamController / DownloadController.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.File.PhysicalPath ?? "";
        // Block uploaded audio tracks — covers are public
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
                var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
                var useRemoteStorage = storageProvider is "s3" or "r2";

                // Helper: upload a demo file to object storage if using S3/R2
                async Task<string> ResolveDemoAudioUrl(string localRelativePath)
                {
                    if (!useRemoteStorage)
                        return localRelativePath; // local storage — serve from wwwroot

                    // e.g. "/audio/demo1.mp3" → try several base directories
                    var fileName = localRelativePath.TrimStart('/');
                    string? localFile = null;
                    var candidates = new[]
                    {
                        Path.Combine(AppContext.BaseDirectory, "wwwroot", "uploads", fileName),
                        Path.Combine(AppContext.BaseDirectory, "wwwroot", fileName),
                        Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", fileName),
                        Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", fileName),
                        Path.Combine("wwwroot", "uploads", fileName),
                        Path.Combine("wwwroot", fileName),
                    };
                    foreach (var c in candidates)
                    {
                        if (File.Exists(c)) { localFile = c; break; }
                    }
                    if (localFile is null)
                    {
                        Console.WriteLine($"[Seed] WARNING: Demo file not found. Tried: {string.Join(", ", candidates)}");
                        return localRelativePath;
                    }

                    var key = $"demos/{fileName}";
                    await using var fs = File.OpenRead(localFile);
                    var storedKey = await storage.UploadAsync(fs, key, "audio/mpeg");
                    Console.WriteLine($"[Seed] Uploaded {localFile} → {storedKey}");
                    return storedKey;
                }

                var demoData = new[]
                {
                    (Title: "Midnight Drive",   Genre: "Lo-Fi",       Price: 29, Duration: "3:42", File: "/audio/demo1.mp3",  NX: 2900, EX: 49900, Desc: "Chill late-night vibes"),
                    (Title: "Neon Skyline",     Genre: "Electronic",  Price: 39, Duration: "4:15", File: "/audio/demo2.mp3",  NX: 3900, EX: 59900, Desc: "Synthwave-inspired beat"),
                    (Title: "Golden Hour",      Genre: "R&B",         Price: 34, Duration: "3:58", File: "/audio/demo3.mp3",  NX: 3400, EX: 54900, Desc: "Smooth R&B groove"),
                    (Title: "Concrete Jungle",  Genre: "Hip-Hop",     Price: 49, Duration: "3:20", File: "/audio/demo4.mp3",  NX: 4900, EX: 74900, Desc: "Hard-hitting trap beat"),
                    (Title: "Summer Breeze",    Genre: "Pop",         Price: 24, Duration: "3:35", File: "/audio/demo5.mp3",  NX: 2400, EX: 39900, Desc: "Upbeat pop instrumental"),
                    (Title: "Rainy Afternoon",  Genre: "Jazz",        Price: 19, Duration: "5:10", File: "/audio/demo6.mp3",  NX: 1900, EX: 29900, Desc: "Smooth jazz session"),
                    (Title: "Electric Dreams",  Genre: "Electronic",  Price: 44, Duration: "4:45", File: "/audio/demo7.mp3",  NX: 4400, EX: 69900, Desc: "Future bass production"),
                    (Title: "Starlight",        Genre: "Indie",       Price: 29, Duration: "4:02", File: "/audio/demo8.mp3",  NX: 2900, EX: 44900, Desc: "Dreamy indie beat"),
                    (Title: "Urban Echoes",     Genre: "Hip-Hop",     Price: 54, Duration: "3:15", File: "/audio/demo9.mp3",  NX: 5400, EX: 84900, Desc: "Atmospheric boom-bap"),
                    (Title: "Velvet Sunset",    Genre: "Lo-Fi",       Price: 22, Duration: "3:50", File: "/audio/demo10.mp3", NX: 2200, EX: 34900, Desc: "Warm lo-fi textures"),
                };

                var tracks = new List<Track>();
                foreach (var d in demoData)
                {
                    var audioUrl = await ResolveDemoAudioUrl(d.File);
                    tracks.Add(new Track
                    {
                        Id = Guid.NewGuid(),
                        Title = d.Title,
                        Genre = d.Genre,
                        Price = d.Price,
                        Duration = d.Duration,
                        CreatorId = creatorId,
                        AudioUrl = audioUrl,
                        NonExclusivePriceCents = d.NX,
                        ExclusivePriceCents = d.EX,
                        Description = d.Desc,
                    });
                }

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
