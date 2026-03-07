using System.Text;
using Cambrian.Api.Common;
using Cambrian.Api.Middleware;
using Microsoft.AspNetCore.Mvc;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Storage;
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

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=cambrian;Username=postgres;Password=postgres";
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

// JWT Authentication
var jwtKey = builder.Configuration["Jwt:Key"] ?? "cambrian-dev-secret-key-min-32-chars!!";
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "cambrian",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "cambrian",
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

// CORS - allow the Vite frontend in development
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173")
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
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddScoped<IUploadService, UploadService>();
builder.Services.AddScoped<IWebhookService, StripeWebhookService>();
builder.Services.AddScoped<IInvoiceService, InvoiceService>();

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
builder.Services.AddSingleton<IObjectStorage, R2ObjectStorage>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<ExceptionMiddleware>();
// app.UseHttpsRedirection(); // disabled for local dev
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

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
