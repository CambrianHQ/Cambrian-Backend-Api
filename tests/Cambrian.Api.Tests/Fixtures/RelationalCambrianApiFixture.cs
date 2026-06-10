using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace Cambrian.Api.Tests.Fixtures;

/// <summary>
/// Relational integration-test host that prefers PostgreSQL via Testcontainers and
/// falls back to in-memory SQLite when Docker is unavailable in the environment.
/// </summary>
public class RelationalCambrianApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TestJwtKey = "cambrian-test-key-1234567890-abcdef";
    private const string TestJwtIssuer = "cambrian-test-issuer";
    private const string TestJwtAudience = "cambrian-test-audience";

    private PostgreSqlContainer? _postgres;
    private DbConnection? _fallbackConnection;
    private string? _connectionString;

    public bool UsingPostgres => _postgres is not null;
    public string DatabaseProvider => UsingPostgres ? "PostgreSQL" : "SQLite";
    public string? FallbackReason { get; private set; }

    public RecordingPaymentGateway PaymentGateway =>
        Services.GetRequiredService<RecordingPaymentGateway>();

    protected virtual bool UseTestWebhookService => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders());

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = TestJwtKey,
                ["Jwt:Issuer"] = TestJwtIssuer,
                ["Jwt:Audience"] = TestJwtAudience,
                ["App:FrontendUrl"] = "http://localhost:5173",
                ["Checkout:RequireSubscription"] = "false",
                ["Stripe:WebhookSecret"] = "whsec_test",
                ["Stripe:Prices:Creator"] = "price_test_creator",
                ["Stripe:Prices:Pro"] = "price_test_pro",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll(typeof(DbContextOptions<CambrianDbContext>));
            services.RemoveAll(typeof(CambrianDbContext));

            if (UsingPostgres)
            {
                services.AddDbContext<CambrianDbContext>(options =>
                    options.UseNpgsql(_connectionString));
            }
            else
            {
                services.AddDbContext<CambrianDbContext>(options =>
                    options.UseSqlite((SqliteConnection)_fallbackConnection!));
            }

            services.RemoveAll<IPaymentGateway>();
            services.RemoveAll<RecordingPaymentGateway>();
            services.AddSingleton<RecordingPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(sp => sp.GetRequiredService<RecordingPaymentGateway>());

            services.RemoveAll<IObjectStorage>();
            services.AddSingleton<IObjectStorage, FakeObjectStorage>();

            if (UseTestWebhookService)
            {
                services.RemoveAll<IWebhookService>();
                services.AddScoped<IWebhookService, TestWebhookService>();
            }
        });
    }

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("cambrian_tests")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .WithCleanUp(true)
                .Build();

            await _postgres.StartAsync();
            _connectionString = _postgres.GetConnectionString();
        }
        catch (Exception ex)
        {
            FallbackReason = ex.Message;
            _postgres = null;
            var sqlite = new SqliteConnection("Data Source=:memory:");
            await sqlite.OpenAsync();
            _fallbackConnection = sqlite;
            _connectionString = sqlite.ConnectionString;
        }

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        if (UsingPostgres)
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.Database.EnsureCreatedAsync();
        }

        await SetFeatureFlagAsync("StripeConnectEnabled", true);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_fallbackConnection is not null)
        {
            await _fallbackConnection.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    public async Task<string> RegisterUserAsync(
        string email,
        string password = "Test1234!@",
        string displayName = "TestUser")
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password,
            displayName
        });
        res.EnsureSuccessStatusCode();

        await MarkUserEmailVerifiedAsync(email);

        var token = await LoginUserAsync(email, password);
        return token;
    }

    public async Task<string> LoginUserAsync(
        string email,
        string password = "Test1234!@")
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("data").GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Auth response did not include a token.");
    }

    public async Task<HttpClient> CreateAuthenticatedClientAsync(
        string email,
        string password = "Test1234!@")
    {
        await RegisterUserAsync(email, password);
        var token = await LoginUserAsync(email, password);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<HttpClient> CreateRoleClientAsync(
        string email,
        string role,
        string username,
        string password = "Test1234!@")
    {
        await RegisterUserAsync(email, password, username);
        await SetUserRoleAsync(email, role);
        await SetUsernameAsync(email, username);

        var token = await LoginUserAsync(email, password);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<string> GetUserIdAsync(string email)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        return user?.Id ?? throw new InvalidOperationException($"User {email} not found.");
    }

    public async Task MarkUserEmailVerifiedAsync(string email)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
            return;

        user.EmailVerified = true;
        await db.SaveChangesAsync();
    }

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
        user.DisplayName ??= username;
        await db.SaveChangesAsync();
    }

    public async Task<Guid> SeedTrackAsync(string creatorId, string title = "Integration Beat")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var trackId = Guid.NewGuid();
        db.Tracks.Add(new Track
        {
            Id = trackId,
            CambrianTrackId = $"CAMB-TRK-{trackId.ToString()[..8].ToUpperInvariant()}",
            Title = title,
            Price = 29.99m,
            NonExclusivePriceCents = 2999,
            ExclusivePriceCents = 12999,
            CopyrightBuyoutPriceCents = 49999,
            LicenseType = "non-exclusive",
            AudioUrl = "tracks/test-beat.mp3",
            CreatorId = creatorId,
            Genre = "Hip-Hop"
        });
        await db.SaveChangesAsync();
        return trackId;
    }

    public async Task SeedCompletedPurchaseAsync(string userId, Guid trackId, string stripeSessionId = "cs_completed_seed")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = userId,
            TrackId = trackId,
            AmountCents = 2999,
            PaymentMethod = "stripe",
            LicenseType = "non-exclusive",
            UsageType = "personal",
            Status = "completed",
            StripeSessionId = stripeSessionId,
            CompletedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

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
                RolloutPercentage = 100
            });
        }
        else
        {
            flag.Enabled = enabled;
        }

        await db.SaveChangesAsync();
    }
}

public sealed class RecordingPaymentGateway : IPaymentGateway
{
    private readonly ConcurrentDictionary<string, CheckoutSessionInfo> _sessions = new();
    private Exception? _nextCreateFailure;

    public void FailNextCreate(Exception exception) => _nextCreateFailure = exception;

    public IReadOnlyCollection<CheckoutSessionInfo> Sessions => _sessions.Values.ToList();

    public Task<string> CreateCheckoutSessionAsync(
        int amountInCents,
        string productName,
        string? clientReferenceId = null,
        string? successUrl = null,
        string? cancelUrl = null,
        string? customerEmail = null)
    {
        if (_nextCreateFailure is not null)
        {
            var ex = _nextCreateFailure;
            _nextCreateFailure = null;
            throw ex;
        }

        var sessionId = $"cs_test_{Guid.NewGuid():N}";
        _sessions[sessionId] = new CheckoutSessionInfo
        {
            SessionId = sessionId,
            Status = "paid",
            ClientReferenceId = clientReferenceId,
            AmountTotal = amountInCents
        };

        return Task.FromResult($"https://checkout.stripe.test/session/{sessionId}");
    }

    public Task<string> CreateSubscriptionCheckoutAsync(
        int amountInCents,
        string planName,
        string clientReferenceId,
        string successUrl,
        string cancelUrl,
        string? customerEmail = null)
    {
        if (_nextCreateFailure is not null)
        {
            var ex = _nextCreateFailure;
            _nextCreateFailure = null;
            throw ex;
        }

        var sessionId = $"cs_sub_{Guid.NewGuid():N}";
        _sessions[sessionId] = new CheckoutSessionInfo
        {
            SessionId = sessionId,
            Status = "paid",
            ClientReferenceId = clientReferenceId,
            AmountTotal = amountInCents
        };

        return Task.FromResult($"https://checkout.stripe.test/subscription/{sessionId}");
    }

    public Task<string> CreateSubscriptionCheckoutByPriceAsync(
        string priceId,
        string clientReferenceId,
        string successUrl,
        string cancelUrl,
        string? customerEmail = null)
    {
        if (_nextCreateFailure is not null)
        {
            var ex = _nextCreateFailure;
            _nextCreateFailure = null;
            throw ex;
        }

        var sessionId = $"cs_sub_{Guid.NewGuid():N}";
        _sessions[sessionId] = new CheckoutSessionInfo
        {
            SessionId = sessionId,
            Status = "paid",
            ClientReferenceId = clientReferenceId,
            AmountTotal = null
        };

        return Task.FromResult($"https://checkout.stripe.test/subscription/{sessionId}");
    }

    public Task<string> EnsureCustomerAsync(string email)
        => Task.FromResult($"cus_test_{email}");

    public Task<string> CreateBillingPortalSessionAsync(string customerId, string returnUrl)
        => Task.FromResult($"https://billing.stripe.test/portal/{customerId}");

    public Task<CheckoutSessionInfo?> GetCheckoutSessionAsync(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task<string> CreateConnectAccountAsync(string email)
        => Task.FromResult("acct_fake_123");

    public Task<string> CreateAccountOnboardingLinkAsync(string accountId, string returnUrl, string refreshUrl)
        => Task.FromResult($"https://connect.stripe.test/onboarding/{accountId}");

    public Task<ConnectAccountStatus> GetConnectAccountStatusAsync(string accountId)
        => Task.FromResult(new ConnectAccountStatus
        {
            AccountId = accountId,
            Status = "active",
            ChargesEnabled = true,
            PayoutsEnabled = true
        });

    public Task<string> CreateExpressDashboardLinkAsync(string accountId)
        => Task.FromResult($"https://connect.stripe.test/dashboard/{accountId}");

    public Task<string> CreateTransferAsync(string destinationAccountId, long amountCents, string description)
        => Task.FromResult($"tr_{Guid.NewGuid():N}");

    public Task DeleteConnectedAccountAsync(string accountId)
        => Task.CompletedTask;

    public Task<string> CreateConnectedCheckoutAsync(
        string connectedAccountId, int amountInCents, string productName, string clientReferenceId,
        string successUrl, string cancelUrl, long applicationFeeCents)
    {
        var sessionId = $"cs_tip_{Guid.NewGuid():N}";
        _sessions[sessionId] = new CheckoutSessionInfo
        {
            SessionId = sessionId,
            Status = "paid",
            ClientReferenceId = clientReferenceId,
            AmountTotal = amountInCents
        };
        return Task.FromResult($"https://checkout.stripe.test/tip/{sessionId}");
    }

    public Task<string> CreateConnectedSubscriptionCheckoutAsync(
        string connectedAccountId, int amountInCents, string productName, string clientReferenceId,
        string successUrl, string cancelUrl, decimal applicationFeePercent)
    {
        var sessionId = $"cs_fansub_{Guid.NewGuid():N}";
        _sessions[sessionId] = new CheckoutSessionInfo
        {
            SessionId = sessionId,
            Status = "paid",
            ClientReferenceId = clientReferenceId,
            AmountTotal = amountInCents
        };
        return Task.FromResult($"https://checkout.stripe.test/fansub/{sessionId}");
    }
}
