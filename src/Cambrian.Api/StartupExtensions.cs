using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Email;
using Cambrian.Infrastructure.Options;
using Cambrian.Infrastructure.Sms;
using Cambrian.Infrastructure.Storage;
using Cambrian.Infrastructure.Stripe;
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
            connectionString = $"Host={uri.Host};Port={port};Database={uri.AbsolutePath.TrimStart('/')};Username={userInfo[0]};Password={userInfo[1]};SSL Mode=Require;Trust Server Certificate=false";
            Console.WriteLine($"[Startup] Parsed DB URI → Host={uri.Host}, Port={port}, DB={uri.AbsolutePath.TrimStart('/')}");
        }

        // Connection pool tuning — Render databases have limited connections (free = 97, basic-256 = 97).
        // Only append defaults when the connection string doesn't already specify each setting.
        if (!connectionString.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("MaxPoolSize", StringComparison.OrdinalIgnoreCase))
        {
            connectionString += ";Maximum Pool Size=20";
        }
        if (!connectionString.Contains("Minimum Pool Size", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("MinPoolSize", StringComparison.OrdinalIgnoreCase))
        {
            connectionString += ";Minimum Pool Size=5";
        }
        if (!connectionString.Contains("Connection Idle Lifetime", StringComparison.OrdinalIgnoreCase))
        {
            connectionString += ";Connection Idle Lifetime=120";
        }
        if (!connectionString.Contains("Connection Pruning Interval", StringComparison.OrdinalIgnoreCase))
        {
            connectionString += ";Connection Pruning Interval=15";
        }
        if (!connectionString.Contains("Keepalive", StringComparison.OrdinalIgnoreCase))
        {
            connectionString += ";Keepalive=60";
        }
        if (!connectionString.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            connectionString += ";Timeout=30";
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
                // Named HttpClient used by S3ObjectStorage to fetch via presigned URLs.
                // Reads go through this HttpClient because AWSSDK.S3 3.7.305 mis-signs direct
                // HTTP calls against path-prefixed S3 endpoints (Supabase /storage/v1/s3/...).
                // The SDK's presigned URL generator produces correct SigV4 query signatures,
                // so we keep the SDK for signing and use HttpClient for transport.
                builder.Services.AddHttpClient("SupabaseStorage", c =>
                {
                    c.Timeout = TimeSpan.FromSeconds(30);
                });
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
        var emailOptions = builder.Configuration.GetSection("Email").Get<EmailOptions>() ?? new EmailOptions();
        var emailProvider = (builder.Configuration["Email:Provider"] ?? emailOptions.Provider ?? "console")
            .ToLowerInvariant();
        switch (emailProvider)
        {
            case "smtp":
                EnsureSenderIdentityConfigured(emailOptions, "smtp");
                if (string.IsNullOrWhiteSpace(emailOptions.SmtpHost))
                    throw new InvalidOperationException(
                        "Email:SmtpHost must be set when Email:Provider is 'smtp'.");
                if (emailOptions.SmtpPort <= 0)
                    throw new InvalidOperationException(
                        "Email:SmtpPort must be a positive integer when Email:Provider is 'smtp'.");
                builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
                break;
            case "resend":
                EnsureSenderIdentityConfigured(emailOptions, "resend");
                if (string.IsNullOrWhiteSpace(emailOptions.ResendApiKey))
                    throw new InvalidOperationException(
                        "Email:ResendApiKey must be set when Email:Provider is 'resend'.");
                builder.Services.AddHttpClient("Resend", client =>
                {
                    client.BaseAddress = new Uri("https://api.resend.com/");
                    client.Timeout = TimeSpan.FromSeconds(15);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Cambrian-Backend-Api");
                });
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

    /// <summary>Register SMS provider (twilio or console).</summary>
    public static void AddSmsProvider(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<SmsOptions>(builder.Configuration.GetSection("Sms"));
        var smsProvider = builder.Configuration["Sms:Provider"]?.ToLowerInvariant() ?? "console";
        switch (smsProvider)
        {
            // Twilio provider can be added here when needed:
            // case "twilio":
            //     builder.Services.AddSingleton<ISmsService, TwilioSmsService>();
            //     break;
            default:
                builder.Services.AddSingleton<ISmsService, ConsoleSmsService>();
                break;
        }
    }

    private static void EnsureSenderIdentityConfigured(EmailOptions emailOptions, string provider)
    {
        if (string.IsNullOrWhiteSpace(emailOptions.FromAddress))
            throw new InvalidOperationException(
                $"Email:FromAddress must be set when Email:Provider is '{provider}'.");

        if (string.IsNullOrWhiteSpace(emailOptions.FromName))
            throw new InvalidOperationException(
                $"Email:FromName must be set when Email:Provider is '{provider}'.");
    }

    public static void AddPaymentGateway(this WebApplicationBuilder builder)
    {
        var stripeKey = builder.Configuration["Stripe:SecretKey"] ?? "";
        if (builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(stripeKey))
        {
            Console.WriteLine("[Startup] Stripe:SecretKey not set — using development payment gateway.");
            builder.Services.AddSingleton<IPaymentGateway, DevelopmentPaymentGateway>();
            return;
        }

        builder.Services.AddSingleton<IPaymentGateway, StripeFacade>();
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
            ? new[] { "https://staging.cambrianmusic.com", "https://api-staging.cambrianmusic.com", "https://cambrian-git-staging-loganbryanxs-projects.vercel.app" }
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

            var remaining = (await db.Database.GetPendingMigrationsAsync()).ToList();
            if (remaining.Count > 0)
            {
                throw new InvalidOperationException(
                    "Database schema is still behind after migration attempt. Remaining pending migrations: "
                    + string.Join(", ", remaining));
            }

            Console.WriteLine("[Startup] Database migrations applied successfully");
        }
        catch (Exception ex)
        {
            app.Logger.LogCritical(ex,
                "Database migration failed. Refusing to start with a stale schema because runtime queries may reference columns that do not exist yet.");
            throw;
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
        await SeedStagingDataAsync(app);
        await SeedCreatorImagesAsync(app);
        await BackfillCreatorProfilesAsync(app);
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
            Console.WriteLine("[INFO] Stripe:SecretKey not set — Development uses a local payment gateway; other non-production environments require explicit Stripe config.");

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
                await CreateAdminAsync(userManager, adminEmail, adminPassword);
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
        string email, string password)
    {
        var admin = new ApplicationUser
        {
            Email = email,
            UserName = email,
            DisplayName = AdminRole,
            Role = AdminRole,
            Tier = "creator",
            EmailConfirmed = true,
            // Seeded admins must be pre-verified or the new VerifiedEmail policy
            // will lock them out of upload/payouts/api-key endpoints.
            EmailVerified = true
        };
        var result = await userManager.CreateAsync(admin, password);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Admin account creation failed: {string.Join("; ", result.Errors.Select(e => e.Description))}");

        Console.WriteLine($"[Seed] Admin account created: {email}");
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
            throw new InvalidOperationException(
                $"Admin password reset failed: {string.Join("; ", pwResult.Errors.Select(e => e.Description))}");

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

        var demoPassword = app.Configuration["SeedDemoUsers:Password"];
        if (string.IsNullOrWhiteSpace(demoPassword))
        {
            Console.WriteLine("[Seed] Skipping demo user seeding (SeedDemoUsers:Password not configured)");
            return;
        }

        var demoUsers = new[]
        {
            ("aiden",     "Aiden Sharp",  Domain.Enums.CreatorTier.Free),
            ("bellanova", "Bella Nova",   Domain.Enums.CreatorTier.Free),
            ("cassius",   "Cassius Reed", Domain.Enums.CreatorTier.Free),
            ("dahlia",    "Dahlia Moon",  Domain.Enums.CreatorTier.Free),
            ("ezra",      "Ezra Voss",    Domain.Enums.CreatorTier.Free),
            ("faye",      "Faye Lark",    Domain.Enums.CreatorTier.Pro),
            ("griffin",   "Griffin Cole", Domain.Enums.CreatorTier.Pro),
            ("harper",    "Harper Wren",  Domain.Enums.CreatorTier.Pro),
            ("indigo",    "Indigo Sage",  Domain.Enums.CreatorTier.Pro),
            ("juniper",   "Juniper Kai",  Domain.Enums.CreatorTier.Pro),
        };

        try
        {
            using var scope = app.Services.CreateScope();
            var userManager = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();

            foreach (var (username, displayName, creatorTier) in demoUsers)
            {
                var email = $"{username}@cambrianmusic.com";
                var existing = await userManager.FindByEmailAsync(email);

                if (existing is null)
                {
                    var user = new ApplicationUser
                    {
                        UserName = username,
                        Email = email,
                        DisplayName = displayName,
                        Role = "Creator",
                        Tier = "creator",
                        CreatorTier = creatorTier,
                        EmailConfirmed = true,
                        // Seeded demo users must be pre-verified — the VerifiedEmail policy
                        // gates upload/checkout/payouts/api-keys.
                        EmailVerified = true,
                        SubscriptionStatus = creatorTier == Domain.Enums.CreatorTier.Pro ? "Active" : "Inactive"
                    };
                    var createResult = await userManager.CreateAsync(user, demoPassword);
                    if (createResult.Succeeded)
                    {
                        Console.WriteLine($"[Seed] Demo user created: {email} ({creatorTier})");
                    }
                    else
                    {
                        Console.WriteLine($"[Seed] Demo user failed: {email} — {string.Join("; ", createResult.Errors.Select(e => e.Description))}");
                    }

                    continue;
                }

                var changed = false;
                if (!string.Equals(existing.UserName, username, StringComparison.Ordinal))
                {
                    existing.UserName = username;
                    changed = true;
                }
                if (!string.Equals(existing.DisplayName, displayName, StringComparison.Ordinal))
                {
                    existing.DisplayName = displayName;
                    changed = true;
                }
                if (!string.Equals(existing.Role, "Creator", StringComparison.OrdinalIgnoreCase))
                {
                    existing.Role = "Creator";
                    changed = true;
                }
                if (!string.Equals(existing.Tier, "creator", StringComparison.OrdinalIgnoreCase))
                {
                    existing.Tier = "creator";
                    changed = true;
                }
                if (existing.CreatorTier != creatorTier)
                {
                    existing.CreatorTier = creatorTier;
                    changed = true;
                }

                var expectedSubscriptionStatus = creatorTier == Domain.Enums.CreatorTier.Pro ? "Active" : "Inactive";
                if (!string.Equals(existing.SubscriptionStatus, expectedSubscriptionStatus, StringComparison.OrdinalIgnoreCase))
                {
                    existing.SubscriptionStatus = expectedSubscriptionStatus;
                    changed = true;
                }

                if (!existing.EmailConfirmed)
                {
                    existing.EmailConfirmed = true;
                    changed = true;
                }

                var passwordResetToken = await userManager.GeneratePasswordResetTokenAsync(existing);
                var passwordResult = await userManager.ResetPasswordAsync(existing, passwordResetToken, demoPassword);
                if (!passwordResult.Succeeded)
                {
                    Console.WriteLine($"[Seed] Demo user password sync failed: {email} — {string.Join("; ", passwordResult.Errors.Select(e => e.Description))}");
                }
                else
                {
                    changed = true;
                }

                if (changed)
                {
                    var updateResult = await userManager.UpdateAsync(existing);
                    if (updateResult.Succeeded)
                        Console.WriteLine($"[Seed] Demo user synced: {email} ({creatorTier})");
                    else
                        Console.WriteLine($"[Seed] Demo user update failed: {email} — {string.Join("; ", updateResult.Errors.Select(e => e.Description))}");
                }
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
                await flagRepo.UpsertAsync("creator_storefront", enabled: true);
                Console.WriteLine("[Seed] Feature flag 'creator_storefront' created (enabled)");
            }
            else if (!existing.Enabled)
            {
                await flagRepo.UpsertAsync("creator_storefront", enabled: true);
                Console.WriteLine("[Seed] Feature flag 'creator_storefront' enabled");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Seed] Feature flag seed error: {ex.Message}");
        }
    }

    /// <summary>
    /// Seed realistic staging data: non-creator users, creator profiles, tracks,
    /// purchases, library items, wallet transactions, subscriptions, and edge cases.
    /// Only runs in Development or Staging when SeedStagingData config is true.
    /// Idempotent — checks for existing data before inserting.
    /// </summary>
    private static async Task SeedStagingDataAsync(WebApplication app)
    {
        if (app.Environment.IsProduction())
            return;

        if (!app.Configuration.GetValue("SeedStagingData", false))
        {
            Console.WriteLine("[Seed] Staging data seeding disabled (set SeedStagingData=true to enable)");
            return;
        }

        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var userManager = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();

            // Skip if already seeded (check for sentinel track)
            if (await db.Tracks.AnyAsync(t => t.CambrianTrackId == "CAMB-TRK-SEED0001"))
            {
                app.Logger.LogWarning("[Seed] Staging data already present — ensuring media placeholders exist");
                var existingTracks = await db.Tracks
                    .Where(t => t.CambrianTrackId.StartsWith("CAMB-TRK-SEED"))
                    .Select(t => new SeedMediaTrackRef
                    {
                        CambrianTrackId = t.CambrianTrackId,
                        CoverArtUrl = t.CoverArtUrl,
                        AudioUrl = t.AudioUrl
                    })
                    .ToListAsync();
                app.Logger.LogWarning("[Seed] Found {Count} seed tracks for media check", existingTracks.Count);
                await EnsureSeedMediaAsync(app, existingTracks);
                return;
            }

            var demoPassword = app.Configuration["SeedDemoUsers:Password"] ?? "***REDACTED***";

            // ── 1. Non-creator users (free listener + paid listener) ──
            var freeListener = await GetOrCreateUserAsync(userManager, new ApplicationUser
            {
                UserName = "listener-free",
                Email = "listener-free@cambrianmusic.com",
                DisplayName = "Free Listener",
                Role = "User",
                Tier = "free",
                CreatorTier = Domain.Enums.CreatorTier.Free,
                EmailConfirmed = true,
                EmailVerified = true,
                SubscriptionStatus = "Inactive"
            }, demoPassword);

            var paidListener = await GetOrCreateUserAsync(userManager, new ApplicationUser
            {
                UserName = "listener-paid",
                Email = "listener-paid@cambrianmusic.com",
                DisplayName = "Paid Listener",
                Role = "User",
                Tier = "paid",
                CreatorTier = Domain.Enums.CreatorTier.Free,
                EmailConfirmed = true,
                EmailVerified = true,
                SubscriptionStatus = "Active"
            }, demoPassword);

            // Heavy library user (edge case)
            var heavyUser = await GetOrCreateUserAsync(userManager, new ApplicationUser
            {
                UserName = "listener-heavy",
                Email = "listener-heavy@cambrianmusic.com",
                DisplayName = "Heavy Library User",
                Role = "User",
                Tier = "paid",
                CreatorTier = Domain.Enums.CreatorTier.Free,
                EmailConfirmed = true,
                EmailVerified = true,
                SubscriptionStatus = "Active"
            }, demoPassword);

            // User with no creator profile (edge case — has creator tier but never set up profile)
            var orphanCreator = await GetOrCreateUserAsync(userManager, new ApplicationUser
            {
                UserName = "creator-noprofile",
                Email = "creator-noprofile@cambrianmusic.com",
                DisplayName = "No Profile Creator",
                Role = "Creator",
                Tier = "creator",
                CreatorTier = Domain.Enums.CreatorTier.Free,
                EmailConfirmed = true,
                EmailVerified = true,
                SubscriptionStatus = "Inactive"
            }, demoPassword);

            // ── 2. Look up demo creators (seeded by SeedDemoUsersAsync) ──
            var aiden = await userManager.FindByEmailAsync("aiden@cambrianmusic.com");
            var bellanova = await userManager.FindByEmailAsync("bellanova@cambrianmusic.com");
            var cassius = await userManager.FindByEmailAsync("cassius@cambrianmusic.com");
            var faye = await userManager.FindByEmailAsync("faye@cambrianmusic.com");
            var griffin = await userManager.FindByEmailAsync("griffin@cambrianmusic.com");
            var juniper = await userManager.FindByEmailAsync("juniper@cambrianmusic.com");

            if (aiden is null || bellanova is null || cassius is null || faye is null)
            {
                Console.WriteLine("[Seed] Demo creator users not found — run SeedDemoUsers first. Skipping staging data.");
                return;
            }

            // ── 3. Creator identity records (Creator entity) ──
            var creatorRecords = new (ApplicationUser User, string Username, string? Bio, string? Niche)[]
            {
                (aiden!, "aiden", "AI cinematic composer. Dark atmospheres and emotional soundscapes.", "cinematic"),
                (bellanova!, "bellanova", "Dreamy electronic producer blending synthwave with ambient textures.", "electronic"),
                (cassius!, "cassius", "Hard-hitting trap and hip-hop beats for the modern era.", "hip-hop"),
                (faye!, "faye", "Lo-fi and chill beats for studying, coding, and relaxing.", "lo-fi"),
                (griffin!, "griffin", "Epic orchestral arrangements for games and film.", "orchestral"),
                // juniper = creator with no tracks (edge case)
                (juniper!, "juniper", "Experimental sound designer. Coming soon.", "experimental"),
            };

            foreach (var (user, username, bio, niche) in creatorRecords)
            {
                if (!await db.Creators.AnyAsync(c => c.UserId == user.Id))
                {
                    db.Creators.Add(new Creator
                    {
                        Id = Guid.NewGuid(),
                        UserId = user.Id,
                        Username = username,
                        DisplayName = user.DisplayName,
                        Bio = bio ?? "",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }

                if (!await db.CreatorProfiles.AnyAsync(cp => cp.UserId == user.Id))
                {
                    db.CreatorProfiles.Add(new CreatorProfile
                    {
                        Id = Guid.NewGuid(),
                        UserId = user.Id,
                        Slug = username,
                        Bio = bio ?? "",
                        Niche = niche,
                        ShowEarnings = false,
                        ShowDownloadStats = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
            }
            await db.SaveChangesAsync();
            Console.WriteLine($"[Seed] Creator profiles created for {creatorRecords.Length} creators");

            // ── 4. Tracks (18 tracks across 5 creators, juniper has none) ──
            var now = DateTime.UtcNow;
            var tracks = new List<Track>();

            // Aiden — cinematic (4 tracks, 1 exclusive sold)
            var aidenCreator = await db.Creators.FirstAsync(c => c.UserId == aiden!.Id);
            tracks.AddRange(new[]
            {
                MakeTrack("CAMB-TRK-SEED0001", "Obsidian Dawn", "cinematic", "dark", aiden!.Id, aidenCreator.Id, 499, 2499, 9999, now.AddDays(-30)),
                MakeTrack("CAMB-TRK-SEED0002", "Neon Cathedral", "cinematic", "energetic", aiden!.Id, aidenCreator.Id, 599, 2999, 14999, now.AddDays(-25)),
                MakeTrack("CAMB-TRK-SEED0003", "Frozen Empire", "cinematic", "dark", aiden!.Id, aidenCreator.Id, 399, 1999, 7999, now.AddDays(-20)),
                MakeTrack("CAMB-TRK-SEED0004", "Starfall", "cinematic", "chill", aiden!.Id, aidenCreator.Id, 449, 2199, 8999, now.AddDays(-10), exclusiveSold: true),
            });

            // Bellanova — electronic (4 tracks)
            var bellaCreator = await db.Creators.FirstAsync(c => c.UserId == bellanova!.Id);
            tracks.AddRange(new[]
            {
                MakeTrack("CAMB-TRK-SEED0005", "Velvet Horizon", "electronic", "chill", bellanova!.Id, bellaCreator.Id, 349, 1799, 6999, now.AddDays(-28)),
                MakeTrack("CAMB-TRK-SEED0006", "Chrome Pulse", "electronic", "energetic", bellanova!.Id, bellaCreator.Id, 399, 1999, 7999, now.AddDays(-22)),
                MakeTrack("CAMB-TRK-SEED0007", "Midnight Signal", "electronic", "dark", bellanova!.Id, bellaCreator.Id, 449, 2199, 8999, now.AddDays(-15)),
                MakeTrack("CAMB-TRK-SEED0008", "Aurora Loop", "electronic", "happy", bellanova!.Id, bellaCreator.Id, 299, 1499, 5999, now.AddDays(-5)),
            });

            // Cassius — hip-hop (3 tracks)
            var cassCreator = await db.Creators.FirstAsync(c => c.UserId == cassius!.Id);
            tracks.AddRange(new[]
            {
                MakeTrack("CAMB-TRK-SEED0009", "Concrete Kingdom", "hip-hop", "energetic", cassius!.Id, cassCreator.Id, 599, 2999, 11999, now.AddDays(-26)),
                MakeTrack("CAMB-TRK-SEED0010", "Shadow Flex", "hip-hop", "dark", cassius!.Id, cassCreator.Id, 499, 2499, 9999, now.AddDays(-18)),
                MakeTrack("CAMB-TRK-SEED0011", "Gold Standard", "hip-hop", "happy", cassius!.Id, cassCreator.Id, 549, 2699, 10999, now.AddDays(-8)),
            });

            // Faye — lo-fi (4 tracks, one with zero sales — edge case)
            var fayeCreator = await db.Creators.FirstAsync(c => c.UserId == faye!.Id);
            tracks.AddRange(new[]
            {
                MakeTrack("CAMB-TRK-SEED0012", "Rainy Window", "lo-fi", "chill", faye!.Id, fayeCreator.Id, 199, 999, 3999, now.AddDays(-27)),
                MakeTrack("CAMB-TRK-SEED0013", "Paper Lantern", "lo-fi", "happy", faye!.Id, fayeCreator.Id, 249, 1299, 4999, now.AddDays(-21)),
                MakeTrack("CAMB-TRK-SEED0014", "Dusty Vinyl", "lo-fi", "chill", faye!.Id, fayeCreator.Id, 199, 999, 3999, now.AddDays(-14)),
                MakeTrack("CAMB-TRK-SEED0015", "Empty Café", "lo-fi", "chill", faye!.Id, fayeCreator.Id, 149, 799, 2999, now.AddDays(-2)), // zero sales
            });

            // Griffin — orchestral (3 tracks)
            var griffCreator = await db.Creators.FirstAsync(c => c.UserId == griffin!.Id);
            tracks.AddRange(new[]
            {
                MakeTrack("CAMB-TRK-SEED0016", "Throne of Light", "orchestral", "energetic", griffin!.Id, griffCreator.Id, 699, 3499, 14999, now.AddDays(-24)),
                MakeTrack("CAMB-TRK-SEED0017", "Echoes of War", "orchestral", "dark", griffin!.Id, griffCreator.Id, 749, 3799, 15999, now.AddDays(-16)),
                MakeTrack("CAMB-TRK-SEED0018", "Fields of Grace", "orchestral", "happy", griffin!.Id, griffCreator.Id, 599, 2999, 11999, now.AddDays(-6)),
            });

            db.Tracks.AddRange(tracks);
            await db.SaveChangesAsync();

            // Update upload counts
            foreach (var creator in new[] { aiden, bellanova, cassius, faye, griffin })
            {
                var count = tracks.Count(t => t.CreatorId == creator!.Id);
                creator!.UploadCount = count;
                await userManager.UpdateAsync(creator);
            }
            Console.WriteLine($"[Seed] {tracks.Count} tracks created across 5 creators");

            // Write placeholder cover art + audio for dev (local) and staging (S3/R2)
            await EnsureSeedMediaAsync(app, tracks.Select(t => new SeedMediaTrackRef
            {
                CambrianTrackId = t.CambrianTrackId,
                CoverArtUrl = t.CoverArtUrl,
                AudioUrl = t.AudioUrl
            }));

            // ── 5. Purchases + library items (realistic flows) ──
            var purchaseData = new List<(ApplicationUser Buyer, Track Track, string LicenseType, string UsageType, int AmountCents, string Status)>
            {
                // Free listener buys a non-exclusive track
                (freeListener!, tracks[0], "non-exclusive", "personal", tracks[0].NonExclusivePriceCents, "completed"),
                // Paid listener buys multiple tracks (various license types)
                (paidListener!, tracks[4], "non-exclusive", "youtube", tracks[4].NonExclusivePriceCents, "completed"),
                (paidListener!, tracks[8], "non-exclusive", "podcast", tracks[8].NonExclusivePriceCents, "completed"),
                (paidListener!, tracks[11], "non-exclusive", "personal", tracks[11].NonExclusivePriceCents, "completed"),
                // Heavy user — buys many tracks
                (heavyUser!, tracks[1], "non-exclusive", "youtube", tracks[1].NonExclusivePriceCents, "completed"),
                (heavyUser!, tracks[5], "non-exclusive", "ads", tracks[5].NonExclusivePriceCents, "completed"),
                (heavyUser!, tracks[9], "non-exclusive", "game", tracks[9].NonExclusivePriceCents, "completed"),
                (heavyUser!, tracks[12], "non-exclusive", "social", tracks[12].NonExclusivePriceCents, "completed"),
                (heavyUser!, tracks[15], "non-exclusive", "film", tracks[15].NonExclusivePriceCents, "completed"),
                (heavyUser!, tracks[17], "non-exclusive", "personal", tracks[17].NonExclusivePriceCents, "completed"),
                // Exclusive purchase (Starfall was exclusive-sold)
                (paidListener!, tracks[3], "exclusive", "film", tracks[3].ExclusivePriceCents, "completed"),
            };

            var purchases = new List<Purchase>();
            var libraryItems = new List<LibraryItem>();
            var walletTxns = new List<WalletTransaction>();
            var creatorById = new[] { aiden, bellanova, cassius, faye, griffin }
                .Where(u => u is not null)
                .ToDictionary(u => u!.Id, u => u!);

            foreach (var (buyer, track, licenseType, usageType, amountCents, status) in purchaseData)
            {
                var purchaseId = Guid.NewGuid();
                var completedAt = track.CreatedAt.AddHours(2);

                purchases.Add(new Purchase
                {
                    Id = purchaseId,
                    BuyerId = buyer.Id,
                    TrackId = track.Id,
                    AmountCents = amountCents,
                    LicenseType = licenseType,
                    UsageType = usageType,
                    Status = status,
                    StripeSessionId = $"cs_test_seed_{purchaseId:N}",
                    CompletedAt = completedAt,
                    CreatedAt = track.CreatedAt.AddHours(1),
                    UpdatedAt = completedAt
                });

                libraryItems.Add(new LibraryItem
                {
                    Id = Guid.NewGuid(),
                    UserId = buyer.Id,
                    TrackId = track.Id,
                    PurchaseId = purchaseId,
                    Title = track.Title,
                    Artist = track.CreatorId, // will map to creator display name in UI
                    SavedAt = completedAt
                });

                // Creator wallet credit — use tier-based fee rate with floor to match CheckoutService
                var creatorFeeRate = creatorById.TryGetValue(track.CreatorId, out var creatorUser)
                    ? Cambrian.Application.Configuration.TierManifest.For(creatorUser.CreatorTier).FeeRate
                    : Cambrian.Application.Configuration.TierManifest.Free.FeeRate;
                walletTxns.Add(new WalletTransaction
                {
                    Id = Guid.NewGuid(),
                    UserId = track.CreatorId,
                    AmountCents = (long)Math.Floor(amountCents * (1 - creatorFeeRate)),
                    Type = "credit",
                    Description = $"Sale: {track.Title} ({licenseType})",
                    RelatedPurchaseId = purchaseId,
                    CreatedAt = completedAt
                });
            }

            db.Purchases.AddRange(purchases);
            db.Library.AddRange(libraryItems);
            db.WalletTransactions.AddRange(walletTxns);
            await db.SaveChangesAsync();
            Console.WriteLine($"[Seed] {purchases.Count} purchases, {libraryItems.Count} library items, {walletTxns.Count} wallet txns created");

            // ── 6. Subscriptions ──
            var subscriptions = new List<Subscription>
            {
                new() { Id = Guid.NewGuid(), UserId = paidListener!.Id, Plan = "paid", Status = "active", StartedAt = now.AddMonths(-3) },
                new() { Id = Guid.NewGuid(), UserId = heavyUser!.Id, Plan = "paid", Status = "active", StartedAt = now.AddMonths(-6) },
                new() { Id = Guid.NewGuid(), UserId = faye!.Id, Plan = "creator", Status = "active", StartedAt = now.AddMonths(-4) },
                new() { Id = Guid.NewGuid(), UserId = griffin!.Id, Plan = "creator", Status = "active", StartedAt = now.AddMonths(-2) },
                // Cancelled subscription (edge case)
                new() { Id = Guid.NewGuid(), UserId = freeListener!.Id, Plan = "paid", Status = "cancelled", StartedAt = now.AddMonths(-5), ExpiresAt = now.AddDays(-15) },
            };
            db.Subscriptions.AddRange(subscriptions);

            // ── 7. Stripe test-mode linkage (test customer IDs) ──
            paidListener!.StripeAccountId = "cus_test_paidlistener";
            heavyUser!.StripeAccountId = "cus_test_heavyuser";
            aiden!.StripeAccountId = "acct_test_aiden";
            faye!.StripeAccountId = "acct_test_faye";
            griffin!.StripeAccountId = "acct_test_griffin";
            await userManager.UpdateAsync(paidListener);
            await userManager.UpdateAsync(heavyUser);
            await userManager.UpdateAsync(aiden);
            await userManager.UpdateAsync(faye);
            await userManager.UpdateAsync(griffin);

            // ── 8. Analytics events (sample data) ──
            var analyticsEvents = new List<AnalyticsEvent>
            {
                new() { Id = Guid.NewGuid(), EventType = "play", UserId = paidListener.Id, TrackId = tracks[0].Id, CreatedAt = now.AddDays(-3) },
                new() { Id = Guid.NewGuid(), EventType = "play", UserId = heavyUser.Id, TrackId = tracks[1].Id, CreatedAt = now.AddDays(-2) },
                new() { Id = Guid.NewGuid(), EventType = "download", UserId = paidListener.Id, TrackId = tracks[4].Id, CreatedAt = now.AddDays(-2) },
                new() { Id = Guid.NewGuid(), EventType = "search", UserId = freeListener.Id, Metadata = "{\"query\":\"lo-fi chill\"}", CreatedAt = now.AddDays(-1) },
                new() { Id = Guid.NewGuid(), EventType = "upload", UserId = aiden.Id, TrackId = tracks[0].Id, CreatedAt = now.AddDays(-30) },
                new() { Id = Guid.NewGuid(), EventType = "purchase", UserId = heavyUser.Id, TrackId = tracks[15].Id, CreatedAt = now.AddDays(-1) },
            };
            db.AnalyticsEvents.AddRange(analyticsEvents);

            // ── 9. Feature flags ──
            var flagRepo = scope.ServiceProvider.GetRequiredService<IFeatureFlagRepository>();
            if (await flagRepo.GetByNameAsync("staging_banner") is null)
                await flagRepo.UpsertAsync("staging_banner", enabled: true);

            await db.SaveChangesAsync();

            Console.WriteLine("[Seed] ── Staging data summary ──");
            Console.WriteLine($"  Users: {await db.Users.CountAsync()} (free={1}, paid={2}, creator={creatorRecords.Length + 1}, admin=config)");
            Console.WriteLine($"  Creators: {await db.Creators.CountAsync()} profiles");
            Console.WriteLine($"  Tracks: {await db.Tracks.CountAsync()} ({tracks.Count} seeded)");
            Console.WriteLine($"  Purchases: {await db.Purchases.CountAsync()}");
            Console.WriteLine($"  Library items: {await db.Library.CountAsync()}");
            Console.WriteLine($"  Wallet txns: {await db.WalletTransactions.CountAsync()}");
            Console.WriteLine($"  Subscriptions: {await db.Subscriptions.CountAsync()}");
            Console.WriteLine("[Seed] Staging data seeding complete ✓");
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "[Seed] Staging data error: {Message}", ex.Message);
        }
    }

    private static async Task<ApplicationUser> GetOrCreateUserAsync(
        Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> userManager,
        ApplicationUser template, string password)
    {
        var existing = await userManager.FindByEmailAsync(template.Email!);
        if (existing is not null)
            return existing;

        var result = await userManager.CreateAsync(template, password);
        if (result.Succeeded)
            Console.WriteLine($"[Seed] User created: {template.Email} (role={template.Role}, tier={template.Tier})");
        else
            Console.WriteLine($"[Seed] User creation failed: {template.Email} — {string.Join("; ", result.Errors.Select(e => e.Description))}");

        return template;
    }

    private static Track MakeTrack(
        string cambrianId, string title, string genre, string mood,
        string creatorId, Guid creatorUuid,
        int nonExcPriceCents, int excPriceCents, int buyoutPriceCents,
        DateTime createdAt, bool exclusiveSold = false)
    {
        // Derive a deterministic audio key from the CambrianTrackId so
        // the path is predictable for R2/S3 uploads: tracks/<slug>.mp3
        var slug = cambrianId.Replace("CAMB-TRK-", "").ToLowerInvariant();
        return new Track
        {
            Id = Guid.NewGuid(),
            CambrianTrackId = cambrianId,
            Title = title,
            Genre = genre,
            Mood = mood,
            Instrumental = true,
            Price = nonExcPriceCents / 100.0m,
            NonExclusivePriceCents = nonExcPriceCents,
            ExclusivePriceCents = excPriceCents,
            CopyrightBuyoutPriceCents = buyoutPriceCents,
            ExclusiveSold = exclusiveSold,
            Status = exclusiveSold ? "exclusive_sold" : "available",
            Visibility = "public",
            Duration = "3:24",
            LicenseType = "non-exclusive",
            CreatorId = creatorId,
            CreatorUuid = creatorUuid,
            AudioUrl = $"tracks/demo-{slug}.mp3",
            CoverArtUrl = $"covers/demo-{slug}.jpg",
            CreatedAt = createdAt
        };
    }

    /// <summary>
    /// Ensure placeholder cover images and audio files exist for seed tracks.
    /// Local storage: writes to disk. S3/R2 storage: uploads via IObjectStorage.
    /// Idempotent — skips files that already exist.
    /// </summary>
    private sealed class SeedMediaTrackRef
    {
        public string CambrianTrackId { get; init; } = "";
        public string? CoverArtUrl { get; init; }
        public string? AudioUrl { get; init; }
    }

    private static async Task EnsureSeedMediaAsync(WebApplication app, IEnumerable<SeedMediaTrackRef> tracks)
    {
        var storageProvider = app.Configuration["Storage:Provider"] ?? "local";
        app.Logger.LogWarning("[Seed] EnsureSeedMediaAsync: provider={Provider}", storageProvider);

        if (string.Equals(storageProvider, "local", StringComparison.OrdinalIgnoreCase))
        {
            EnsureSeedMediaLocal(app, tracks);
            return;
        }

        // ── S3/R2 path: upload placeholders via IObjectStorage ──
        using var scope = app.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();

        // Minimal valid 1×1 white-pixel JPEG (107 bytes)
        var jpegPlaceholder = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////"
            + "2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB"
            + "/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/xAAUAQEAAAAAAAAAAAAAAAAAAAAA/8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/a"
            + "AAwDAQACEQMRAD8AKwA//9k=");

        // Minimal valid MP3: single MPEG1 Layer3 frame, 128kbps, 44100Hz, ~26ms silence.
        // Built directly to avoid base64 encoding issues.
        var mp3Placeholder = new byte[417];
        mp3Placeholder[0] = 0xFF; // Sync byte 1
        mp3Placeholder[1] = 0xFB; // Sync byte 2 + MPEG1, Layer3, no CRC
        mp3Placeholder[2] = 0x90; // 128kbps, 44100Hz
        mp3Placeholder[3] = 0x00; // Padding=0, Stereo
        // Remaining bytes are zero (silence)

        var uploaded = 0;
        var skipped = 0;
        var errors = 0;

        foreach (var track in tracks)
        {
            // Upload cover art
            if (!string.IsNullOrWhiteSpace(track.CoverArtUrl))
            {
                try
                {
                    var existing = await storage.OpenReadAsync(track.CoverArtUrl);
                    if (existing is not null)
                    {
                        existing.Dispose();
                        skipped++;
                    }
                    else
                    {
                        using var coverStream = new MemoryStream(jpegPlaceholder);
                        await storage.UploadAsync(coverStream, track.CoverArtUrl, "image/jpeg");
                        uploaded++;
                    }
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "[Seed] Failed to seed cover: key={Key}", track.CoverArtUrl);
                    errors++;
                }
            }

            // Upload audio
            if (!string.IsNullOrWhiteSpace(track.AudioUrl))
            {
                try
                {
                    var existing = await storage.OpenReadAsync(track.AudioUrl);
                    if (existing is not null)
                    {
                        existing.Dispose();
                        skipped++;
                    }
                    else
                    {
                        using var audioStream = new MemoryStream(mp3Placeholder);
                        await storage.UploadAsync(audioStream, track.AudioUrl, "audio/mpeg");
                        uploaded++;
                    }
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "[Seed] Failed to seed audio: key={Key}", track.AudioUrl);
                    errors++;
                }
            }
        }

        app.Logger.LogWarning("[Seed] S3 placeholder media: {Uploaded} uploaded, {Skipped} already existed, {Errors} errors",
            uploaded, skipped, errors);
    }

    /// <summary>
    /// Write placeholder media to local disk for development.
    /// </summary>
    private static void EnsureSeedMediaLocal(WebApplication app, IEnumerable<SeedMediaTrackRef> tracks)
    {
        var basePath = app.Configuration["Storage:LocalPath"] ?? "wwwroot/uploads";
        var coversDir = Path.Combine(app.Environment.ContentRootPath, basePath, "covers");
        var tracksDir = Path.Combine(app.Environment.ContentRootPath, basePath, "tracks");
        Directory.CreateDirectory(coversDir);
        Directory.CreateDirectory(tracksDir);

        // Minimal valid 1×1 white-pixel JPEG (107 bytes)
        var jpegPlaceholder = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////"
            + "2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB"
            + "/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/xAAUAQEAAAAAAAAAAAAAAAAAAAAA/8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/a"
            + "AAwDAQACEQMRAD8AKwA//9k=");

        // Minimal valid MP3 frame (silent)
        var mp3Placeholder = Convert.FromBase64String(
            "//uQxAAAAAANIAAAAAExBTUUzLjEwMFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV"
            + "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV"
            + "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV"
            + "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV"
            + "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVf/7kMQPAAADSAAAAABMQU1FMy4x"
            + "MDBVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV"
            + "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV"
            + "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV"
            + "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVQ==");

        foreach (var track in tracks)
        {
            if (!string.IsNullOrWhiteSpace(track.CoverArtUrl))
            {
                var fileName = Path.GetFileName(track.CoverArtUrl);
                var filePath = Path.Combine(coversDir, fileName);
                if (!File.Exists(filePath))
                    File.WriteAllBytes(filePath, jpegPlaceholder);
            }

            if (!string.IsNullOrWhiteSpace(track.AudioUrl))
            {
                var fileName = Path.GetFileName(track.AudioUrl);
                var filePath = Path.Combine(tracksDir, fileName);
                if (!File.Exists(filePath))
                    File.WriteAllBytes(filePath, mp3Placeholder);
            }
        }

        Console.WriteLine($"[Seed] Placeholder media written to {basePath}");
    }

    /// <summary>
    /// One-time backfill: assign unique profile and banner images to existing creators
    /// that currently have no images. New users are unaffected — they upload their own.
    /// Idempotent: only fills NULL/empty image URLs.
    /// </summary>
    private static async Task SeedCreatorImagesAsync(WebApplication app)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

            // ── One-time guard: skip if already run ──
            const string flagName = "creator_images_seeded_v2";
            var flag = await db.FeatureFlags.FirstOrDefaultAsync(f => f.Name == flagName);
            if (flag is { Enabled: true })
            {
                Console.WriteLine("[Seed] Creator image backfill already completed — skipping");
                return;
            }

            // ── Palette of unique banner photos (landscape, music/studio themed) ──
            // Using picsum.photos with deterministic seeds for stable, unique images.
            var bannerSeeds = new[]
            {
                "studio-neon", "vinyl-sunset", "soundwave-blue", "concert-red",
                "headphones-purple", "guitar-amber", "piano-mono", "synth-teal",
                "drums-fire", "mic-gold", "bass-ocean", "dj-night",
                "stage-lights", "mixing-desk", "vinyl-warm", "keys-cool",
                "speakers-glow", "amp-vintage", "strings-dawn", "beats-dusk"
            };

            // ── Palette of unique avatar styles (DiceBear "initials" + distinct backgrounds) ──
            var avatarColors = new[]
            {
                "c0392b", "2980b9", "27ae60", "8e44ad", "d35400",
                "16a085", "2c3e50", "f39c12", "1abc9c", "e74c3c",
                "3498db", "9b59b6", "e67e22", "1e8449", "6c3483",
                "cb4335", "2e86c1", "229954", "a569bd", "dc7633"
            };

            var idx = 0;

            // ── Update Creator table ──
            var creators = await db.Creators
                .Where(c => c.ProfileImageUrl == null || c.ProfileImageUrl == ""
                         || c.CoverImageUrl == null || c.CoverImageUrl == "")
                .ToListAsync();

            foreach (var creator in creators)
            {
                var seed = idx % bannerSeeds.Length;
                var color = avatarColors[idx % avatarColors.Length];
                var name = Uri.EscapeDataString(creator.DisplayName ?? creator.Username);

                if (string.IsNullOrEmpty(creator.ProfileImageUrl))
                    creator.ProfileImageUrl = $"https://api.dicebear.com/9.x/initials/svg?seed={name}&backgroundColor={color}";

                if (string.IsNullOrEmpty(creator.CoverImageUrl))
                    creator.CoverImageUrl = $"https://picsum.photos/seed/{bannerSeeds[seed]}/1500/500";

                creator.UpdatedAt = DateTime.UtcNow;
                idx++;
            }

            // ── Create missing CreatorProfile rows + update existing ones ──
            var allCreators = await db.Creators.ToListAsync();
            var existingProfileUserIds = await db.CreatorProfiles
                .Select(p => p.UserId)
                .ToListAsync();

            var createdProfiles = 0;
            foreach (var creator in allCreators)
            {
                if (existingProfileUserIds.Contains(creator.UserId))
                    continue;

                var cIdx = allCreators.IndexOf(creator) % bannerSeeds.Length;
                var cColor = avatarColors[allCreators.IndexOf(creator) % avatarColors.Length];
                var cName = Uri.EscapeDataString(creator.DisplayName ?? creator.Username);

                db.CreatorProfiles.Add(new CreatorProfile
                {
                    Id = Guid.NewGuid(),
                    UserId = creator.UserId,
                    Slug = creator.Username,
                    Bio = creator.Bio,
                    ProfileImageUrl = $"https://api.dicebear.com/9.x/initials/svg?seed={cName}&backgroundColor={cColor}",
                    BannerImageUrl = $"https://picsum.photos/seed/{bannerSeeds[cIdx]}/1500/500",
                    ShowEarnings = false,
                    ShowDownloadStats = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                createdProfiles++;
            }

            // Update existing profiles that have missing images
            var profiles = await db.CreatorProfiles
                .Where(p => p.ProfileImageUrl == null || p.ProfileImageUrl == ""
                         || p.BannerImageUrl == null || p.BannerImageUrl == "")
                .ToListAsync();

            foreach (var profile in profiles)
            {
                var matchingCreator = allCreators.FirstOrDefault(c => c.UserId == profile.UserId);
                var profileIdx = matchingCreator != null ? allCreators.IndexOf(matchingCreator) : idx++;
                var seed = profileIdx % bannerSeeds.Length;
                var color = avatarColors[profileIdx % avatarColors.Length];
                var name = Uri.EscapeDataString(profile.Slug);

                if (string.IsNullOrEmpty(profile.ProfileImageUrl))
                    profile.ProfileImageUrl = $"https://api.dicebear.com/9.x/initials/svg?seed={name}&backgroundColor={color}";

                if (string.IsNullOrEmpty(profile.BannerImageUrl))
                    profile.BannerImageUrl = $"https://picsum.photos/seed/{bannerSeeds[seed]}/1500/500";

                profile.UpdatedAt = DateTime.UtcNow;
            }

            if (creators.Count > 0 || profiles.Count > 0 || createdProfiles > 0)
            {
                // Mark as done so this never runs again
                if (flag is null)
                    db.FeatureFlags.Add(new FeatureFlag { Id = Guid.NewGuid(), Name = flagName, Enabled = true, RolloutPercentage = 100 });
                else
                    flag.Enabled = true;

                await db.SaveChangesAsync();
                Console.WriteLine($"[Seed] Creator images backfilled: {creators.Count} creator(s), {profiles.Count} updated profile(s), {createdProfiles} new profile(s)");
            }
            else
            {
                // Nothing to do but still mark complete to avoid re-querying every startup
                if (flag is null)
                    db.FeatureFlags.Add(new FeatureFlag { Id = Guid.NewGuid(), Name = flagName, Enabled = true, RolloutPercentage = 100 });
                else
                    flag.Enabled = true;
                await db.SaveChangesAsync();
                Console.WriteLine("[Seed] Creator images: all creators already have images — marked complete");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Seed] Creator image backfill skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Backfill Creator and CreatorProfile rows for every track creator in the database.
    /// Ensures every creator's catalog/storefront is accessible. Idempotent — skips
    /// creators that already have both rows.
    /// </summary>
    private static async Task BackfillCreatorProfilesAsync(WebApplication app)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

            // Run once — skip if already completed
            var flag = await db.FeatureFlags.FirstOrDefaultAsync(f => f.Name == "backfill_creator_profiles_done");
            if (flag is not null && flag.Enabled)
            {
                Console.WriteLine("[Backfill] Creator profile backfill already completed — skipping.");
                return;
            }

            // Find all distinct creator user IDs from tracks
            var creatorUserIds = await db.Tracks
                .AsNoTracking()
                .Where(t => t.CreatorId != null && t.Status != "removed")
                .Select(t => t.CreatorId!)
                .Distinct()
                .ToListAsync();

            if (creatorUserIds.Count == 0)
            {
                Console.WriteLine("[Backfill] No track creators found — skipping.");
                return;
            }

            var backfilledCreators = 0;
            var backfilledProfiles = 0;

            foreach (var userId in creatorUserIds)
            {
                // Look up the ApplicationUser
                var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
                if (user is null) continue;

                // Derive a username: prefer Identity UserName (if not email), else email prefix
                var username = !string.IsNullOrWhiteSpace(user.UserName)
                    && !string.Equals(user.UserName, user.Email, StringComparison.OrdinalIgnoreCase)
                    ? user.UserName.Trim().ToLowerInvariant()
                    : (user.Email ?? user.Id).Split('@')[0].Trim().ToLowerInvariant();

                // Ensure Creator row exists
                var hasCreator = await db.Creators.AnyAsync(c => c.UserId == userId);
                if (!hasCreator)
                {
                    // Check username uniqueness — append user ID fragment if taken
                    var slugCandidate = username;
                    if (await db.Creators.AnyAsync(c => c.Username == slugCandidate))
                        slugCandidate = $"{username}-{userId[..8]}";

                    db.Creators.Add(new Creator
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        Username = slugCandidate,
                        DisplayName = user.DisplayName ?? username,
                        Bio = "",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                    backfilledCreators++;
                    // Update username for profile slug below
                    username = slugCandidate;
                }
                else
                {
                    // Use existing creator's username for profile slug
                    var existing = await db.Creators.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId);
                    if (existing is not null && !string.IsNullOrWhiteSpace(existing.Username))
                        username = existing.Username;
                }

                // Ensure CreatorProfile row exists
                var hasProfile = await db.CreatorProfiles.AnyAsync(cp => cp.UserId == userId);
                if (!hasProfile)
                {
                    db.CreatorProfiles.Add(new CreatorProfile
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        Slug = username,
                        Bio = "",
                        ShowEarnings = false,
                        ShowDownloadStats = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                    backfilledProfiles++;
                }
            }

            if (backfilledCreators > 0 || backfilledProfiles > 0)
            {
                await db.SaveChangesAsync();
                Console.WriteLine($"[Backfill] Creator profiles: {backfilledCreators} Creator rows, {backfilledProfiles} CreatorProfile rows created for {creatorUserIds.Count} track creators.");
            }
            else
            {
                Console.WriteLine($"[Backfill] All {creatorUserIds.Count} track creators already have Creator + CreatorProfile rows.");
            }

            // Mark complete so this never runs again
            if (flag is null)
            {
                db.FeatureFlags.Add(new FeatureFlag
                {
                    Id = Guid.NewGuid(),
                    Name = "backfill_creator_profiles_done",
                    Enabled = true,
                    RolloutPercentage = 100
                });
            }
            else
            {
                db.FeatureFlags.Attach(flag);
                flag.Enabled = true;
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Backfill] Creator profile backfill failed (non-fatal): {ex.Message}");
        }
    }
}
