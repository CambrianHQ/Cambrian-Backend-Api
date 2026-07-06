using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Tests.Fixtures;

/// <summary>
/// Shared test server using in-memory SQLite.
/// Replaces Postgres, Stripe, and R2 with test stubs.
/// </summary>
public class CambrianApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TestJwtKey = "cambrian-test-key-1234567890-abcdef";
    private const string TestJwtIssuer = "cambrian-test-issuer";
    private const string TestJwtAudience = "cambrian-test-audience";

    private SqliteConnection _connection = null!;

    protected virtual bool UseTestWebhookService => true;

    protected virtual IReadOnlyDictionary<string, string?> CreateTestConfigurationOverrides() =>
        new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
        });

        // Provide required secrets so Program.cs startup validation passes
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Jwt:Key"] = TestJwtKey,
                ["Jwt:Issuer"] = TestJwtIssuer,
                ["Jwt:Audience"] = TestJwtAudience,
                ["Checkout:RequireSubscription"] = "false",
                ["Stripe:Prices:Creator"] = "price_test_creator",
                ["Stripe:Prices:Pro"] = "price_test_pro",
                ["App:FrontendUrl"] = "https://app.cambrian.test",
            };

            foreach (var pair in CreateTestConfigurationOverrides())
            {
                settings[pair.Key] = pair.Value;
            }

            config.AddInMemoryCollection(settings);
        });

        builder.ConfigureServices(services =>
        {
            // ---------- Replace Postgres with SQLite in-memory ----------
            services.RemoveAll(typeof(DbContextOptions<CambrianDbContext>));
            services.RemoveAll(typeof(CambrianDbContext));

            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            // Disable FK enforcement so unit-style tests can insert orphaned rows
            using var fkOff = _connection.CreateCommand();
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();

            services.AddDbContext<CambrianDbContext>(options =>
                options.UseSqlite(_connection));

            // ---------- Replace external infrastructure ----------
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway, FakePaymentGateway>();

            services.RemoveAll<IObjectStorage>();
            services.AddSingleton<IObjectStorage, FakeObjectStorage>();

            if (UseTestWebhookService)
            {
                // ---------- Replace webhook service with test version ----------
                // Bypasses Stripe signature verification while preserving all
                // business logic via StripeWebhookService.ProcessEventAsync.
                services.RemoveAll<IWebhookService>();
                services.AddScoped<IWebhookService, TestWebhookService>();
            }
        });
    }

    public async Task InitializeAsync()
    {
        // Ensure the database schema exists
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        await db.Database.EnsureCreatedAsync();
        await SetFeatureFlagAsync("StripeConnectEnabled", true);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    // ----- Helpers -----

    /// <summary>
    /// Register a new user and return the JWT token. Newly-registered users are
    /// automatically marked email-verified so existing tests that hit endpoints
    /// gated by the "VerifiedEmail" policy continue to work without per-test
    /// verification ceremony.
    /// </summary>
    public async Task<string> RegisterUserAsync(
        string email = "test@cambrian.com",
        string password = "Test1234!@")
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password,
            displayName = "TestUser"
        });
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        // Auth endpoints now return ApiResponse envelope: { success, data: { token, ... } }
        var data = json.GetProperty("data");
        var token = data.GetProperty("token").GetString()!;

        // Mirror the production verification flow for tests: mark the user as
        // verified directly in the DB. The returned token still has the original
        // email_verified=false claim — callers needing a fresh token (e.g.
        // CreateAuthenticatedClientAsync) should re-login.
        await MarkUserEmailVerifiedAsync(email);

        return token;
    }

    /// <summary>Login an existing user and return the JWT token.</summary>
    public async Task<string> LoginUserAsync(
        string email = "test@cambrian.com",
        string password = "Test1234!@")
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        return data.GetProperty("token").GetString()!;
    }

    /// <summary>
    /// Register + return an HttpClient whose Bearer token has the email_verified
    /// claim set, so it can hit endpoints gated by the "VerifiedEmail" policy.
    /// </summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync(
        string email = "test@cambrian.com",
        string password = "Test1234!@")
    {
        await RegisterUserAsync(email, password);
        // Re-login so the JWT carries the freshly set email_verified=true claim.
        var token = await LoginUserAsync(email, password);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<HttpClient> CreateRoleClientAsync(
        string email,
        string password,
        string role,
        string? username = null)
    {
        await RegisterUserAsync(email, password);
        await SetUserRoleAsync(email, role);

        if (!string.IsNullOrWhiteSpace(username))
        {
            await SetUsernameAsync(email, username);
        }

        var token = await LoginUserAsync(email, password);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Mark a user's EmailVerified flag in the DB (test verification shortcut).</summary>
    public async Task MarkUserEmailVerifiedAsync(string email)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is not null && !user.EmailVerified)
        {
            user.EmailVerified = true;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Set a user's EmailVerified flag to an explicit value (verify or un-verify).</summary>
    public async Task SetEmailVerifiedAsync(string email, bool verified)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email);
        user.EmailVerified = verified;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Register + return an HttpClient whose Bearer token has email_verified=false,
    /// optionally with a role and username. Used to prove the "VerifiedEmail" policy
    /// blocks unverified accounts from high-stakes endpoints.
    /// </summary>
    public async Task<HttpClient> CreateUnverifiedClientAsync(
        string email,
        string password = "Test1234!@",
        string? role = null,
        string? username = null)
    {
        await RegisterUserAsync(email, password);
        if (!string.IsNullOrWhiteSpace(role))
            await SetUserRoleAsync(email, role);
        if (!string.IsNullOrWhiteSpace(username))
            await SetUsernameAsync(email, username);

        // Un-verify AFTER registration (RegisterUserAsync marks verified) so the
        // freshly-issued login token carries email_verified=false.
        await SetEmailVerifiedAsync(email, false);

        var token = await LoginUserAsync(email, password);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Read a user's current Role string from the DB.</summary>
    public async Task<string> GetUserRoleAsync(string email)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email);
        return user.Role;
    }

    /// <summary>True when a Creator row exists for the given user id.</summary>
    public async Task<bool> CreatorRowExistsAsync(string userId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return await db.Creators.AnyAsync(c => c.UserId == userId);
    }

    /// <summary>Seed a track into the database and return its id.</summary>
    public async Task<Guid> SeedTrackAsync(string creatorId, string title = "Test Beat", string visibility = "public")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var trackId = Guid.NewGuid();
        var track = new Track
        {
            Id = trackId,
            CambrianTrackId = $"CAMB-TRK-{trackId.ToString()[..8].ToUpper()}",
            Title = title,
            Price = 29.99m,
            LicenseType = "standard",
            AudioUrl = "tracks/test-beat.mp3",
            CreatorId = creatorId,
            Genre = "Hip-Hop",
            Visibility = visibility
        };

        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        return track.Id;
    }

    /// <summary>Get the user id for an email.</summary>
    public async Task<string> GetUserIdAsync(string email)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        return user?.Id ?? throw new InvalidOperationException($"User {email} not found");
    }

    /// <summary>Set a user's CreatorTier (drives the plan feature-flag gate).</summary>
    public async Task SetCreatorTierAsync(string email, Cambrian.Domain.Enums.CreatorTier tier)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email);
        user.CreatorTier = tier;
        await db.SaveChangesAsync();
    }

    /// <summary>Grant a role to a user.</summary>
    public async Task SetUserRoleAsync(string email, string role)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email);
        user.Role = role;
        await db.SaveChangesAsync();
    }

    public async Task SetUsernameAsync(string email, string username)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email);
        user.UserName = username;
        user.NormalizedUserName = username.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(user.DisplayName))
        {
            user.DisplayName = username;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Add a LibraryItem directly to the database.</summary>
    public async Task SeedLibraryItemAsync(string userId, Guid trackId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        db.Library.Add(new LibraryItem
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TrackId = trackId,
            Title = "Seeded Track",
            Artist = "Seeded Artist"
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Add a completed Purchase directly to the database (satisfies C3 entitlement check).</summary>
    public async Task SeedCompletedPurchaseAsync(string userId, Guid trackId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = userId,
            TrackId = trackId,
            AmountCents = 5000,
            PaymentMethod = "stripe",
            LicenseType = "nonexclusive",
            Status = "completed",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Enable or disable a feature flag in the test database.</summary>
    public async Task SetFeatureFlagAsync(string name, bool enabled)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var flag = await db.FeatureFlags.FirstOrDefaultAsync(f => f.Name == name);
        if (flag is null)
        {
            db.FeatureFlags.Add(new FeatureFlag
            {
                Id = Guid.NewGuid(),
                Name = name,
                Enabled = enabled,
                RolloutPercentage = 100,
            });
        }
        else
        {
            flag.Enabled = enabled;
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Seed a Creator entity and return its UUID.</summary>
    public async Task<Guid> SeedCreatorAsync(string userId, string username, string? displayName = null)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var id = Guid.NewGuid();
        var normalized = username.Trim().ToLowerInvariant();
        db.Creators.Add(new Creator
        {
            Id = id,
            UserId = userId,
            Username = normalized,
            DisplayName = displayName ?? normalized,
            Bio = "",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Seed a track with the UUID-based CreatorUuid FK set.</summary>
    public async Task<Guid> SeedTrackWithCreatorUuidAsync(string creatorUserId, Guid creatorUuid, string title = "Test Beat", string visibility = "public")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var trackId = Guid.NewGuid();
        db.Tracks.Add(new Track
        {
            Id = trackId,
            CambrianTrackId = $"CAMB-TRK-{trackId.ToString()[..8].ToUpper()}",
            Title = title,
            Price = 29.99m,
            LicenseType = "standard",
            AudioUrl = "tracks/test-beat.mp3",
            CreatorId = creatorUserId,
            CreatorUuid = creatorUuid,
            Genre = "Hip-Hop",
            Visibility = visibility,
        });
        await db.SaveChangesAsync();
        return trackId;
    }
}

// ---------- Fakes ----------

internal sealed class FakePaymentGateway : IPaymentGateway
{
    public Task<string> CreateCheckoutSessionAsync(
        int amountInCents,
        string productName,
        string? clientReferenceId = null,
        string? successUrl = null,
        string? cancelUrl = null,
        string? customerEmail = null)
    {
        // Return a deterministic fake URL so checkout tests can verify redirects
        return Task.FromResult($"https://checkout.stripe.com/fake?ref={clientReferenceId}");
    }

    public Task<string> CreateSubscriptionCheckoutAsync(
        int amountInCents,
        string planName,
        string clientReferenceId,
        string successUrl,
        string cancelUrl,
        string? customerEmail = null)
    {
        return Task.FromResult($"https://checkout.stripe.com/fake-sub?plan={planName}&ref={clientReferenceId}");
    }

    public Task<string> CreateSubscriptionCheckoutByPriceAsync(
        string priceId,
        string clientReferenceId,
        string successUrl,
        string cancelUrl,
        string? customerEmail = null)
    {
        return Task.FromResult($"https://checkout.stripe.com/fake-sub?price={priceId}&ref={clientReferenceId}");
    }

    public Task<string> EnsureCustomerAsync(string email)
        => Task.FromResult($"cus_fake_{email}");

    public Task<string> CreateBillingPortalSessionAsync(string customerId, string returnUrl)
        => Task.FromResult($"https://billing.stripe.com/fake-portal?customer={customerId}");

    public Task<CheckoutSessionInfo?> GetCheckoutSessionAsync(string sessionId)
    {
        // Return a fake paid session for testing
        return Task.FromResult<CheckoutSessionInfo?>(new CheckoutSessionInfo
        {
            SessionId = sessionId,
            Status = "paid",
            ClientReferenceId = null,
            AmountTotal = 0
        });
    }

    // ── Stripe Connect fakes ──

    public Task<string> CreateConnectAccountAsync(string email)
        => Task.FromResult("acct_fake_123");

    public Task<string> CreateAccountOnboardingLinkAsync(string accountId, string returnUrl, string refreshUrl)
        => Task.FromResult($"https://connect.stripe.com/fake-onboarding?account={accountId}");

    public Task<ConnectAccountStatus> GetConnectAccountStatusAsync(string accountId)
        => Task.FromResult(new ConnectAccountStatus
        {
            AccountId = accountId,
            Status = "active",
            ChargesEnabled = true,
            PayoutsEnabled = true
        });

    public Task<string> CreateExpressDashboardLinkAsync(string accountId)
        => Task.FromResult($"https://connect.stripe.com/fake-dashboard?account={accountId}");

    public Task<string> CreateTransferAsync(
        string destinationAccountId, long amountCents, string description, string idempotencyKey)
        => Task.FromResult("tr_fake_123");

    public Task DeleteConnectedAccountAsync(string accountId)
        => Task.CompletedTask;

    // ── Connect money-in fakes ──
    // Recorded so tests can assert the fee parameters passed to the gateway.
    public sealed record ConnectedCheckoutCall(
        string AccountId, int AmountCents, string ClientReferenceId, long ApplicationFeeCents);

    public sealed record ConnectedSubscriptionCall(
        string AccountId, int AmountCents, string ClientReferenceId, decimal ApplicationFeePercent);

    public List<ConnectedCheckoutCall> ConnectedCheckouts { get; } = new();
    public List<ConnectedSubscriptionCall> ConnectedSubscriptions { get; } = new();

    public Task<string> CreateConnectedCheckoutAsync(
        string connectedAccountId, int amountInCents, string productName, string clientReferenceId,
        string successUrl, string cancelUrl, long applicationFeeCents)
    {
        ConnectedCheckouts.Add(new ConnectedCheckoutCall(connectedAccountId, amountInCents, clientReferenceId, applicationFeeCents));
        return Task.FromResult($"https://checkout.stripe.com/fake-tip?account={connectedAccountId}&ref={clientReferenceId}");
    }

    public Task<string> CreateConnectedSubscriptionCheckoutAsync(
        string connectedAccountId, int amountInCents, string productName, string clientReferenceId,
        string successUrl, string cancelUrl, decimal applicationFeePercent)
    {
        ConnectedSubscriptions.Add(new ConnectedSubscriptionCall(connectedAccountId, amountInCents, clientReferenceId, applicationFeePercent));
        return Task.FromResult($"https://checkout.stripe.com/fake-fansub?account={connectedAccountId}&ref={clientReferenceId}");
    }
}

internal sealed class FakeObjectStorage : IObjectStorage
{
    private readonly ConcurrentDictionary<string, StoredObject> _objects = new();

    public async Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg")
    {
        using var copy = new MemoryStream();
        await file.CopyToAsync(copy);
        _objects[key] = new StoredObject(copy.ToArray(), contentType);
        return $"fake://{key}";
    }

    public string GenerateSignedUrl(string key)
        => $"https://fake-cdn.cambrian.test/{key}?signed=true";

    public string GetPublicUrl(string key)
        => $"https://fake-cdn.cambrian.test/{key}";

    public Task<StorageFile?> OpenReadAsync(string key)
    {
        if (_objects.TryGetValue(key, out var stored))
        {
            return Task.FromResult<StorageFile?>(new StorageFile
            {
                Stream = new MemoryStream(stored.Bytes),
                ContentType = stored.ContentType,
                Length = stored.Bytes.Length
            });
        }

        return Task.FromResult<StorageFile?>(new StorageFile
        {
            Stream = new MemoryStream(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }),
            ContentType = "audio/mpeg",
            Length = 4
        });
    }

    public Task DeleteAsync(string key)
        => Task.CompletedTask;

    private sealed record StoredObject(byte[] Bytes, string ContentType);
}
