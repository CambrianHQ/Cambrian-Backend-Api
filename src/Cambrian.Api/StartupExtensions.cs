using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Email;
using Cambrian.Infrastructure.Options;
using Cambrian.Infrastructure.Storage;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api;

internal static class StartupExtensions
{
    private const string TestingEnvironment = "Testing";
    private const string AdminRole = "Admin";

    /// <summary>Resolve database connection string from config, env vars, or fallback for testing.</summary>
    public static string ResolveConnectionString(this WebApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (builder.Environment.EnvironmentName == TestingEnvironment)
                connectionString = builder.Configuration.GetConnectionString("TestConnection")
                    ?? "Host=localhost;Port=5432;Database=cambrian_test;Username=postgres;Password=postgres"; // NOSONAR — local dev/test only
            else
                throw new InvalidOperationException(
                    "Database connection string must be set via ConnectionStrings:DefaultConnection, DATABASE_URL, "
                    + "or 'dotnet user-secrets set ConnectionStrings:DefaultConnection <value>'.");
        }
        Console.WriteLine($"[Startup] DB connection source: {(connectionString.StartsWith("postgres") ? "URI" : "ADO.NET")}");

        // Render provides postgres:// URI — convert to Npgsql ADO.NET format
        if (connectionString.StartsWith("postgres://") || connectionString.StartsWith("postgresql://"))
        {
            var uri = new Uri(connectionString);
            var userInfo = uri.UserInfo.Split(':');
            var port = uri.Port > 0 ? uri.Port : 5432;
            connectionString = $"Host={uri.Host};Port={port};Database={uri.AbsolutePath.TrimStart('/')};Username={userInfo[0]};Password={userInfo[1]};SSL Mode=Require;Trust Server Certificate=true";
            Console.WriteLine($"[Startup] Parsed DB URI → Host={uri.Host}, Port={port}, DB={uri.AbsolutePath.TrimStart('/')}");
        }

        return connectionString;
    }

    /// <summary>Validate and resolve JWT key, Stripe key, and frontend URL.</summary>
    /// <returns>(resolvedJwtKey, isNonProd)</returns>
    public static (string jwtKey, bool isNonProd) ValidateSecrets(this WebApplicationBuilder builder)
    {
        Console.WriteLine($"[Startup] Environment: {builder.Environment.EnvironmentName}");
        var jwtKey = builder.Configuration["Jwt:Key"]
            ?? Environment.GetEnvironmentVariable("Jwt__Key")
            ?? Environment.GetEnvironmentVariable("JWT_KEY")
            ?? "";
        Console.WriteLine($"[Startup] JWT key present: {!string.IsNullOrWhiteSpace(jwtKey)} (len={jwtKey.Length})");
        var isNonProd = builder.Environment.IsDevelopment()
            || builder.Environment.EnvironmentName == TestingEnvironment
            || builder.Environment.EnvironmentName == "Staging";

        if (string.IsNullOrWhiteSpace(jwtKey))
        {
            if (builder.Environment.EnvironmentName == TestingEnvironment)
                jwtKey = "cambrian-test-secret-key-min-32-chars!!";
            else
                throw new InvalidOperationException(
                    "Jwt:Key must be set via environment variable, secret store, "
                    + "or 'dotnet user-secrets set Jwt:Key <value>'.");
        }
        if (jwtKey.Length < 32)
            throw new InvalidOperationException("Jwt:Key must be at least 32 characters.");

        builder.Configuration["Jwt:Key"] = jwtKey;

        ValidateStripeKey(builder, isNonProd);
        ValidateFrontendUrl(builder);

        return (jwtKey, isNonProd);
    }

    /// <summary>Register object storage provider (S3/R2 or local).</summary>
    public static void AddStorageProvider(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
        var storageProvider = builder.Configuration["Storage:Provider"]?.ToLowerInvariant() ?? "local";
        Console.WriteLine($"[Startup] Storage provider: {storageProvider}");
        switch (storageProvider)
        {
            case "s3":
            case "r2":
                var endpoint = builder.Configuration["Storage:Endpoint"] ?? "";
                var bucket = builder.Configuration["Storage:Bucket"] ?? "";
                var accessKey = builder.Configuration["Storage:AccessKey"] ?? "";
                var secretKey = builder.Configuration["Storage:SecretKey"] ?? "";
                if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(bucket)
                    || string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
                {
                    if (builder.Environment.IsProduction())
                        throw new InvalidOperationException(
                            $"Storage provider '{storageProvider}' requires Storage:Endpoint, Storage:Bucket, "
                            + "Storage:AccessKey, and Storage:SecretKey to be configured.");
                    Console.WriteLine($"[WARN] Storage provider '{storageProvider}' credentials incomplete — falling back to local storage.");
                    builder.Services.AddSingleton<IObjectStorage, LocalObjectStorage>();
                    break;
                }
                Console.WriteLine($"[Startup] S3 endpoint={endpoint}, bucket={bucket}");
                builder.Services.AddSingleton<IObjectStorage, S3ObjectStorage>();
                break;
            default:
                if (builder.Environment.IsProduction())
                    throw new InvalidOperationException(
                        "Storage:Provider must be 's3' or 'r2' in Production. Local storage causes data loss on container restart.");
                builder.Services.AddSingleton<IObjectStorage, LocalObjectStorage>();
                break;
        }
    }

    /// <summary>Register email provider (SMTP, Resend, or console).</summary>
    public static void AddEmailProvider(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
        var emailProvider = builder.Configuration["Email:Provider"]?.ToLowerInvariant() ?? "console";
        switch (emailProvider)
        {
            case "smtp":
                builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
                break;
            case "resend":
                builder.Services.AddHttpClient("Resend");
                builder.Services.AddSingleton<IEmailService, ResendEmailService>();
                break;
            default:
                if (builder.Environment.IsProduction())
                    throw new InvalidOperationException(
                        "Email:Provider must be 'smtp' or 'resend' in Production. Console email does not deliver messages to users.");
                builder.Services.AddSingleton<IEmailService, ConsoleEmailService>();
                break;
        }
    }

    /// <summary>Configure CORS origin matching including Vercel/Cloudflare previews.</summary>
    public static void AddCorsPolicy(this WebApplicationBuilder builder)
    {
        var corsOrigins = builder.Configuration.GetSection("App:CorsOrigins").Value?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? Array.Empty<string>();
        var frontendUrl = builder.Configuration["App:FrontendUrl"] ?? "";
        if (builder.Environment.IsProduction() && string.IsNullOrWhiteSpace(frontendUrl))
            throw new InvalidOperationException(
                "App:FrontendUrl must be set in Production (e.g. https://cambrianmusic.com).");

        var defaultOrigins = builder.Environment.IsDevelopment()
            ? new[] { "http://localhost:5173", "http://localhost:5174", "http://localhost:4174", "http://127.0.0.1:4174", "http://127.0.0.1:5173", "http://127.0.0.1:5174" }
            : Array.Empty<string>();
        var productionOrigins = builder.Environment.IsProduction()
            ? new[] { "https://cambrianmusic.com", "https://www.cambrianmusic.com" }
            : Array.Empty<string>();
        var stagingOrigins = builder.Environment.EnvironmentName == "Staging"
            ? new[] { "https://staging.cambrianmusic.com", "https://api-staging.cambrianmusic.com" }
            : Array.Empty<string>();

        var allOrigins = defaultOrigins
            .Concat(corsOrigins)
            .Concat(productionOrigins)
            .Concat(stagingOrigins)
            .Concat(string.IsNullOrWhiteSpace(frontendUrl) ? Array.Empty<string>() : new[] { frontendUrl })
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Distinct()
            .ToArray();

        Console.WriteLine($"[Startup] CORS origins: {string.Join(", ", allOrigins)}");

        var vercelSlug = builder.Configuration["App:VercelProjectSlug"] ?? "";
        var cfSlug = builder.Configuration["App:CloudflarePagesSlug"] ?? "";

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                var originSet = new HashSet<string>(allOrigins, StringComparer.OrdinalIgnoreCase);
                policy.SetIsOriginAllowed(origin => IsOriginAllowed(origin, originSet, vercelSlug, cfSlug))
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();
            });
        });
    }

    /// <summary>Apply pending migrations (skipped for Testing environment).</summary>
    public static async Task RunMigrationsAsync(this WebApplication app)
    {
        if (app.Environment.EnvironmentName == TestingEnvironment)
        {
            Console.WriteLine("[Startup] Skipping migrations (Testing environment uses in-memory DB)");
            return;
        }
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

            // Log schema compatibility info (safe details only)
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            Console.WriteLine($"[Startup] Database schema — {applied.Count} migration(s) applied");
            if (applied.Count > 0)
                Console.WriteLine($"[Startup] Latest migration: {applied[^1]}");

            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
            if (pending.Count > 0)
            {
                Console.WriteLine($"[Startup] Applying {pending.Count} pending migration(s):");
                foreach (var m in pending)
                    Console.WriteLine($"  - {m}");
            }
            await db.Database.MigrateAsync();
            Console.WriteLine("[Startup] Database migrations applied successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] Migration error: {ex.Message}");
        }
    }

    /// <summary>Seed admin user and feature flags from configuration.</summary>
    public static async Task SeedDataAsync(this WebApplication app)
    {
        if (app.Environment.EnvironmentName == TestingEnvironment)
            return;

        await SeedAdminAsync(app);
        await SeedDemoUsersAsync(app);
        await SeedFeatureFlagsAsync(app);
    }

    private static bool IsOriginAllowed(string origin, HashSet<string> originSet, string vercelSlug, string cfSlug)
    {
        if (originSet.Contains(origin))
            return true;

        // SECURITY: Use strict prefix matching to prevent attacker-created subdomains
        // (e.g., "evil-cambrian-app.vercel.app") from passing the CORS check
        if (!string.IsNullOrEmpty(vercelSlug)
            && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Host.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase)
            && (uri.Host.StartsWith(vercelSlug + "-", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals(vercelSlug + ".vercel.app", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!string.IsNullOrEmpty(cfSlug)
            && Uri.TryCreate(origin, UriKind.Absolute, out var cfUri)
            && cfUri.Host.EndsWith(".pages.dev", StringComparison.OrdinalIgnoreCase)
            && (cfUri.Host.StartsWith(cfSlug + "-", StringComparison.OrdinalIgnoreCase)
                || cfUri.Host.Equals(cfSlug + ".pages.dev", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private static void ValidateStripeKey(WebApplicationBuilder builder, bool isNonProd)
    {
        var stripeKey = builder.Configuration["Stripe:SecretKey"] ?? "";
        if (!string.IsNullOrWhiteSpace(stripeKey))
        {
            Stripe.StripeConfiguration.ApiKey = stripeKey;
            if (builder.Environment.IsProduction() && stripeKey.StartsWith("sk_test_", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Production must not use Stripe test keys. Set Stripe:SecretKey to a live key (sk_live_).");
            if (isNonProd && stripeKey.StartsWith("sk_live_"))
                Console.WriteLine("[WARN] Non-production environment is using a LIVE Stripe key — real charges will be processed!");
        }
        else if (!isNonProd)
            throw new InvalidOperationException(
                "Stripe:SecretKey must be configured in Production. Payment endpoints require a live key.");
        else
            Console.WriteLine("[INFO] Stripe:SecretKey not set — payment endpoints will return mock responses.");

        // Validate webhook secret is configured in production to prevent signature bypass
        var webhookSecret = builder.Configuration["Stripe:WebhookSecret"] ?? "";
        if (builder.Environment.IsProduction() && string.IsNullOrWhiteSpace(webhookSecret))
            throw new InvalidOperationException(
                "Stripe:WebhookSecret must be configured in Production. "
                + "Without it, webhook signature verification is bypassed, allowing spoofed events.");
    }

    private static void ValidateFrontendUrl(WebApplicationBuilder builder)
    {
        if (builder.Environment.IsProduction()
            && string.IsNullOrWhiteSpace(builder.Configuration["App:FrontendUrl"]))
            throw new InvalidOperationException(
                "App:FrontendUrl must be configured in Production. Without it, Stripe checkout redirects and email links will be broken.");
    }

    private static async Task SeedAdminAsync(WebApplication app)
    {
        var adminEmail = app.Configuration["Admin:Email"];
        var adminPassword = app.Configuration["Admin:Password"];
        Console.WriteLine($"[Seed] Admin config — email={adminEmail ?? "(null)"}, passwordLength={adminPassword?.Length ?? 0}");

        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
            return;

        try
        {
            using var scope = app.Services.CreateScope();
            var userManager = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();

            var existing = await userManager.FindByEmailAsync(adminEmail);
            Console.WriteLine($"[Seed] FindByEmailAsync result: {(existing is null ? "NOT FOUND" : $"FOUND id={existing.Id} role={existing.Role}")}");

            if (existing is null)
            {
                await CreateAdminAsync(userManager, adminEmail);
            }
            else
            {
                await SyncAdminAsync(userManager, existing, adminEmail, adminPassword);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Seed] Admin seed error: {ex.Message}");
        }
    }

    private static async Task CreateAdminAsync(
        Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> userManager,
        string email)
    {
        var admin = new ApplicationUser
        {
            Email = email,
            UserName = email,
            DisplayName = AdminRole,
            Role = AdminRole,
            Tier = "creator",
            EmailConfirmed = true
        };
        var password = Environment.GetEnvironmentVariable("Admin__Password")
            ?? throw new InvalidOperationException("Admin password not available");
        var result = await userManager.CreateAsync(admin, password);
        if (result.Succeeded)
            Console.WriteLine($"[Seed] Admin account created: {email}");
        else
            Console.WriteLine($"[Seed] Admin creation failed: {string.Join("; ", result.Errors.Select(e => e.Description))}");
    }

    private static async Task SyncAdminAsync(
        Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> userManager,
        ApplicationUser existing, string adminEmail, string adminPassword)
    {
        var changed = false;
        if (existing.Role != AdminRole)
        {
            existing.Role = AdminRole;
            changed = true;
        }
        var token = await userManager.GeneratePasswordResetTokenAsync(existing);
        var pwResult = await userManager.ResetPasswordAsync(existing, token, adminPassword);
        if (!pwResult.Succeeded)
            Console.WriteLine($"[Seed] Admin password sync failed: {string.Join("; ", pwResult.Errors.Select(e => e.Description))}");
        else
            changed = true;

        if (changed)
        {
            await userManager.UpdateAsync(existing);
            Console.WriteLine($"[Seed] Admin account synced: {adminEmail} (role=Admin, password updated)");
        }
        else
        {
            Console.WriteLine($"[Seed] Admin account already up to date: {adminEmail}");
        }
    }

    private static async Task SeedDemoUsersAsync(WebApplication app)
    {
        // SECURITY: Never seed demo users with known passwords in production
        if (app.Environment.IsProduction())
        {
            Console.WriteLine("[Seed] Skipping demo user seeding (Production environment)");
            return;
        }

        var demoUsers = new[]
        {
            ("aiden",    "Aiden Sharp"),
            ("bellanova","Bella Nova"),
            ("cassius",  "Cassius Reed"),
            ("dahlia",   "Dahlia Moon"),
            ("ezra",     "Ezra Voss"),
            ("faye",     "Faye Lark"),
            ("griffin",  "Griffin Cole"),
            ("harper",   "Harper Wren"),
            ("indigo",   "Indigo Sage"),
            ("juniper",  "Juniper Kai"),
        };
        const string defaultPassword = "Cambrian2026!";

        try
        {
            using var scope = app.Services.CreateScope();
            var userManager = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();

            foreach (var (username, displayName) in demoUsers)
            {
                var email = $"{username}@cambrianmusic.com";
                var existing = await userManager.FindByEmailAsync(email);
                if (existing is not null)
                    continue;

                var user = new ApplicationUser
                {
                    UserName = username,
                    Email = email,
                    DisplayName = displayName,
                    Role = "Creator",
                    Tier = "creator",
                    CreatorTier = Domain.Enums.CreatorTier.Free,
                    EmailConfirmed = true
                };
                var result = await userManager.CreateAsync(user, defaultPassword);
                if (result.Succeeded)
                    Console.WriteLine($"[Seed] Demo user created: {email} ({displayName})");
                else
                    Console.WriteLine($"[Seed] Demo user failed: {email} — {string.Join("; ", result.Errors.Select(e => e.Description))}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Seed] Demo user seed error: {ex.Message}");
        }
    }

    private static async Task SeedFeatureFlagsAsync(WebApplication app)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var flagRepo = scope.ServiceProvider.GetRequiredService<IFeatureFlagRepository>();
            var existing = await flagRepo.GetByNameAsync("creator_storefront");
            if (existing is null)
            {
                await flagRepo.UpsertAsync("creator_storefront", enabled: false);
                Console.WriteLine("[Seed] Feature flag 'creator_storefront' created (disabled)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Seed] Feature flag seed error: {ex.Message}");
        }
    }
}
