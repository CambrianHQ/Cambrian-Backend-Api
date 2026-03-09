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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cambrian.Api.Tests.Fixtures;

/// <summary>
/// Shared test server using in-memory SQLite.
/// Replaces Postgres, Stripe, and R2 with test stubs.
/// </summary>
public sealed class CambrianApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private SqliteConnection _connection = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // ---------- Replace Postgres with SQLite in-memory ----------
            services.RemoveAll(typeof(DbContextOptions<CambrianDbContext>));
            services.RemoveAll(typeof(CambrianDbContext));

            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            services.AddDbContext<CambrianDbContext>(options =>
                options.UseSqlite(_connection));

            // ---------- Replace external infrastructure ----------
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway, FakePaymentGateway>();

            services.RemoveAll<IObjectStorage>();
            services.AddSingleton<IObjectStorage, FakeObjectStorage>();
        });
    }

    public async Task InitializeAsync()
    {
        // Ensure the database schema exists
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    // ----- Helpers -----

    /// <summary>Register a new user and return the JWT token.</summary>
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
        return json.GetProperty("token").GetString()!;
    }

    /// <summary>Register + return an HttpClient with the Bearer token pre-set.</summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync(
        string email = "test@cambrian.com",
        string password = "Test1234!@")
    {
        var token = await RegisterUserAsync(email, password);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Seed a track into the database and return its id.</summary>
    public async Task<Guid> SeedTrackAsync(string creatorId, string title = "Test Beat")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var track = new Track
        {
            Id = Guid.NewGuid(),
            Title = title,
            Price = 29.99,
            LicenseType = "standard",
            AudioUrl = "tracks/test-beat.mp3",
            CreatorId = creatorId,
            Genre = "Hip-Hop"
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

    /// <summary>Grant a role to a user.</summary>
    public async Task SetUserRoleAsync(string email, string role)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email);
        user.Role = role;
        await db.SaveChangesAsync();
    }

    /// <summary>Update a user's subscription tier claim source.</summary>
    public async Task SetUserTierAsync(string email, string tier)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email);
        user.Tier = tier;
        await db.SaveChangesAsync();
    }

    /// <summary>Grant both legacy role-based and current tier-based creator access.</summary>
    public async Task PromoteToCreatorAsync(string email)
    {
        await SetUserRoleAsync(email, "Creator");
        await SetUserTierAsync(email, "creator");
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
}

// ---------- Fakes ----------

internal sealed class FakePaymentGateway : IPaymentGateway
{
    public Task<string> CreateCheckoutSessionAsync(
        int amountInCents,
        string productName,
        string? clientReferenceId = null,
        string? successUrl = null,
        string? cancelUrl = null)
    {
        // Return a deterministic fake URL so checkout tests can verify redirects
        return Task.FromResult($"https://checkout.stripe.com/fake?ref={clientReferenceId}");
    }

    public Task<string> CreateSubscriptionCheckoutAsync(
        int amountInCents,
        string planName,
        string clientReferenceId,
        string successUrl,
        string cancelUrl)
    {
        return Task.FromResult($"https://checkout.stripe.com/fake-subscription?ref={clientReferenceId}");
    }
}

internal sealed class FakeObjectStorage : IObjectStorage
{
    public Task<string> UploadAsync(Stream file, string key, string contentType = "audio/mpeg")
        => Task.FromResult($"fake://{key}");

    public string GenerateSignedUrl(string key)
        => $"https://fake-cdn.cambrian.test/{key}?signed=true";

    public Task DeleteAsync(string key)
        => Task.CompletedTask;
}
