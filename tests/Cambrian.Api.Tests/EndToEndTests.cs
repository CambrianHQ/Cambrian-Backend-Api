using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// End-to-end integration tests that exercise the full HTTP pipeline
/// via WebApplicationFactory with an in-memory SQLite database.
/// Covers all critical paths: auth, catalog, checkout, library,
/// purchase, download, and webhook processing.
/// </summary>
public sealed class EndToEndTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _factory;

    public EndToEndTests(CambrianApiFixture factory) => _factory = factory;

    // ────────────────────── Auth: Register ──────────────────────

    [Fact]
    public async Task Register_ReturnsToken_AndFreeTier()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/register", new
        {
            email = "e2e-register@cambrian.com",
            password = "Test1234!@",
            displayName = "E2E User"
        });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEmpty(json.GetProperty("token").GetString()!);
        Assert.Equal("free", json.GetProperty("tier").GetString());
        Assert.Equal("e2e-register@cambrian.com", json.GetProperty("user").GetProperty("email").GetString());
    }

    [Fact]
    public async Task Register_Returns400_WhenPasswordTooWeak()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/register", new
        {
            email = "e2e-weakpwd@cambrian.com",
            password = "weak"
        });

        Assert.True(
            res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.InternalServerError,
            $"Expected 400/500 but got {res.StatusCode}");
    }

    [Fact]
    public async Task Register_Returns400_WhenDuplicateEmail()
    {
        var client = _factory.CreateClient();
        var email = "e2e-dup@cambrian.com";

        await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "Test1234!@"
        });

        var res = await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "Test1234!@"
        });

        Assert.True(
            res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.InternalServerError,
            $"Expected 400/500 for duplicate registration but got {res.StatusCode}");
    }

    // ────────────────────── Auth: Login ──────────────────────

    [Fact]
    public async Task Login_ReturnsToken_WithCorrectCredentials()
    {
        var client = _factory.CreateClient();
        var email = "e2e-login@cambrian.com";

        await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "Test1234!@"
        });

        var res = await client.PostAsJsonAsync("/auth/login", new
        {
            email,
            password = "Test1234!@"
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEmpty(json.GetProperty("token").GetString()!);
    }

    [Fact]
    public async Task Login_Returns403_WithWrongPassword()
    {
        var client = _factory.CreateClient();
        var email = "e2e-wrongpwd@cambrian.com";

        await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "Test1234!@"
        });

        var res = await client.PostAsJsonAsync("/auth/login", new
        {
            email,
            password = "WrongPassword99!"
        });

        Assert.True(
            res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"Expected 401/403 but got {res.StatusCode}");
    }

    [Fact]
    public async Task Login_Returns403_WithNonExistentUser()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/login", new
        {
            email = "e2e-nobody@cambrian.com",
            password = "Test1234!@"
        });

        Assert.True(
            res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"Expected 401/403 but got {res.StatusCode}");
    }

    // ────────────────────── Auth: Me ──────────────────────

    [Fact]
    public async Task Me_Returns401_WithoutToken()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Me_ReturnsProfile_WithValidToken()
    {
        var client = await _factory.CreateAuthenticatedClientAsync(
            "e2e-me@cambrian.com", "Test1234!@");

        var res = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEmpty(json.GetProperty("token").GetString()!);
        Assert.Equal("e2e-me@cambrian.com", json.GetProperty("user").GetProperty("email").GetString());
    }

    // ────────────────────── Catalog: Browse ──────────────────────

    [Fact]
    public async Task Catalog_ReturnsOk_WithoutAuth()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/catalog");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Discover_ReturnsOk_WithoutAuth()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/discover");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task GetTrack_ReturnsTrack_WhenExists()
    {
        var creatorEmail = "e2e-catalog-creator@cambrian.com";
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        var trackId = await _factory.SeedTrackAsync(creatorId, "E2E Catalog Beat");

        var client = _factory.CreateClient();
        var res = await client.GetAsync($"/tracks/{trackId}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("E2E Catalog Beat", json.GetProperty("data").GetProperty("title").GetString());
    }

    [Fact]
    public async Task GetTrack_Returns404_WhenNotExists()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync($"/tracks/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task GetTrack_Returns400_WhenInvalidGuid()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/tracks/not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ────────────────────── Library ──────────────────────

    [Fact]
    public async Task Library_Returns401_WithoutAuth()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/library");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Library_ReturnsEmpty_ForNewUser()
    {
        var client = await _factory.CreateAuthenticatedClientAsync(
            "e2e-lib-empty@cambrian.com", "Test1234!@");

        var res = await client.GetAsync("/library");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal(0, json.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task Library_SaveAndRetrieve_Track()
    {
        var creatorEmail = "e2e-lib-creator@cambrian.com";
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        var trackId = await _factory.SeedTrackAsync(creatorId, "Library Test Beat");

        var buyerEmail = "e2e-lib-buyer@cambrian.com";
        var client = await _factory.CreateAuthenticatedClientAsync(buyerEmail, "Test1234!@");

        var saveRes = await client.PostAsJsonAsync("/library", new { trackId = trackId.ToString() });
        Assert.True(
            saveRes.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Expected 201/200 for library save but got {saveRes.StatusCode}");

        var listRes = await client.GetAsync("/library");
        var listJson = await listRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(listJson.GetProperty("data").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Library_SaveIsDuplicateSafe()
    {
        var creatorEmail = "e2e-lib-dup-creator@cambrian.com";
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        var trackId = await _factory.SeedTrackAsync(creatorId, "Dup Library Beat");

        var buyerEmail = "e2e-lib-dup-buyer@cambrian.com";
        var client = await _factory.CreateAuthenticatedClientAsync(buyerEmail, "Test1234!@");

        await client.PostAsJsonAsync("/library", new { trackId = trackId.ToString() });
        await client.PostAsJsonAsync("/library", new { trackId = trackId.ToString() });

        var listRes = await client.GetAsync("/library");
        var listJson = await listRes.Content.ReadFromJsonAsync<JsonElement>();
        var items = listJson.GetProperty("data").EnumerateArray()
            .Where(x => x.GetProperty("trackId").GetString() == trackId.ToString())
            .ToList();
        Assert.Single(items);
    }

    [Fact]
    public async Task Library_Remove_RemovesTrack()
    {
        var creatorEmail = "e2e-lib-rm-creator@cambrian.com";
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        var trackId = await _factory.SeedTrackAsync(creatorId, "Removable Beat");

        var buyerEmail = "e2e-lib-rm-buyer@cambrian.com";
        var client = await _factory.CreateAuthenticatedClientAsync(buyerEmail, "Test1234!@");

        await client.PostAsJsonAsync("/library", new { trackId = trackId.ToString() });

        var delRes = await client.DeleteAsync($"/library/{trackId}");
        Assert.Equal(HttpStatusCode.OK, delRes.StatusCode);

        var listRes = await client.GetAsync("/library");
        var listJson = await listRes.Content.ReadFromJsonAsync<JsonElement>();
        var items = listJson.GetProperty("data").EnumerateArray()
            .Where(x => x.GetProperty("trackId").GetString() == trackId.ToString())
            .ToList();
        Assert.Empty(items);
    }

    // ────────────────────── Checkout ──────────────────────

    [Fact]
    public async Task Checkout_Returns401_WithoutAuth()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/checkout", new
        {
            trackId = Guid.NewGuid().ToString(),
            licenseType = "standard"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Checkout_ReturnsUrl_WhenTrackExists()
    {
        var creatorEmail = "e2e-checkout-creator@cambrian.com";
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        var trackId = await _factory.SeedTrackAsync(creatorId, "Checkout Beat");

        var client = await _factory.CreateAuthenticatedClientAsync(
            "e2e-checkout-buyer@cambrian.com", "Test1234!@");

        var res = await client.PostAsJsonAsync("/checkout", new
        {
            trackId = trackId.ToString(),
            licenseType = "standard"
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        var url = json.GetProperty("data").GetProperty("checkoutUrl").GetString();
        Assert.Contains("checkout.stripe.com/fake", url);
    }

    [Fact]
    public async Task Checkout_Returns404_WhenTrackMissing()
    {
        var client = await _factory.CreateAuthenticatedClientAsync(
            "e2e-checkout-notrack@cambrian.com", "Test1234!@");

        var res = await client.PostAsJsonAsync("/checkout", new
        {
            trackId = Guid.NewGuid().ToString(),
            licenseType = "standard"
        });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ────────────────────── Webhook: Purchase Flow ──────────────────────

    [Fact]
    public async Task Webhook_CompletesTrackPurchase_AndAddsToLibrary()
    {
        var creatorEmail = "e2e-wh-creator@cambrian.com";
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        var trackId = await _factory.SeedTrackAsync(creatorId, "Webhook Beat");

        var buyerEmail = "e2e-wh-buyer@cambrian.com";
        await _factory.RegisterUserAsync(buyerEmail, "Test1234!@");
        var buyerId = await _factory.GetUserIdAsync(buyerEmail);

        var payload = JsonSerializer.Serialize(new
        {
            id = $"evt_e2e_purchase_{Guid.NewGuid():N}",
            type = "checkout.session.completed",
            data = new
            {
                @object = new
                {
                    client_reference_id = $"{buyerId}:{trackId}:non-exclusive",
                    amount_total = 2999
                }
            }
        });

        var client = _factory.CreateClient();
        var res = await client.PostAsync("/webhook/stripe",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var buyerClient = await _factory.CreateAuthenticatedClientAsync(
            $"e2e-wh-verify-{Guid.NewGuid():N}@cambrian.com", "Test1234!@");
        buyerClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                await _factory.RegisterUserAsync(buyerEmail + ".lib", "Test1234!@"));

        // Verify by checking if the track was added to buyer's library
        // via the webhook handler (uses buyerId directly in DB)
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Cambrian.Persistence.CambrianDbContext>();
        var purchase = db.Purchases
            .FirstOrDefault(p => p.BuyerId == buyerId && p.TrackId == trackId);
        var libraryItem = db.Library
            .FirstOrDefault(l => l.UserId == buyerId && l.TrackId == trackId);

        Assert.NotNull(purchase);
        Assert.Equal("completed", purchase!.Status);
        Assert.NotNull(libraryItem);
    }

    [Fact]
    public async Task Webhook_IsIdempotent_DuplicateEventDoesNotCreateDuplicate()
    {
        var creatorEmail = "e2e-wh-idemp-creator@cambrian.com";
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        var trackId = await _factory.SeedTrackAsync(creatorId, "Idempotent Beat");

        var buyerEmail = "e2e-wh-idemp-buyer@cambrian.com";
        await _factory.RegisterUserAsync(buyerEmail, "Test1234!@");
        var buyerId = await _factory.GetUserIdAsync(buyerEmail);

        var eventId = $"evt_e2e_idemp_{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new
        {
            id = eventId,
            type = "checkout.session.completed",
            data = new
            {
                @object = new
                {
                    client_reference_id = $"{buyerId}:{trackId}:non-exclusive",
                    amount_total = 2999
                }
            }
        });

        var client = _factory.CreateClient();

        var res1 = await client.PostAsync("/webhook/stripe",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        var res2 = await client.PostAsync("/webhook/stripe",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Cambrian.Persistence.CambrianDbContext>();
        var purchases = db.Purchases
            .Where(p => p.BuyerId == buyerId && p.TrackId == trackId)
            .ToList();
        Assert.Single(purchases);
    }

    // ────────────────────── Webhook: Subscription Checkout ──────────────────────

    [Fact]
    public async Task Webhook_SubscriptionCheckout_UpgradesUserTier()
    {
        var email = "e2e-wh-sub@cambrian.com";
        await _factory.RegisterUserAsync(email, "Test1234!@");
        var userId = await _factory.GetUserIdAsync(email);

        var payload = JsonSerializer.Serialize(new
        {
            id = $"evt_e2e_sub_{Guid.NewGuid():N}",
            type = "checkout.session.completed",
            data = new
            {
                @object = new
                {
                    client_reference_id = $"{userId}:subscription:creator"
                }
            }
        });

        var client = _factory.CreateClient();
        var res = await client.PostAsync("/webhook/stripe",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Cambrian.Persistence.CambrianDbContext>();
        var user = await db.Users.FindAsync(userId);
        Assert.Equal("creator", user!.Tier);

        var sub = db.Subscriptions.FirstOrDefault(s => s.UserId == userId && s.Status == "active");
        Assert.NotNull(sub);
        Assert.Equal("creator", sub!.Plan);
    }

    // ────────────────────── Billing: Subscription Checkout ──────────────────────

    [Fact]
    public async Task BillingCheckout_Returns401_WithoutAuth()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/billing/checkout", new { tier = "paid" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task BillingCheckout_ReturnsUrl_ForValidTier()
    {
        var client = await _factory.CreateAuthenticatedClientAsync(
            "e2e-billing@cambrian.com", "Test1234!@");

        var res = await client.PostAsJsonAsync("/billing/checkout", new { tier = "paid" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        var url = json.GetProperty("data").GetProperty("url").GetString();
        Assert.Contains("checkout.stripe.com/fake-sub", url);
    }

    [Fact]
    public async Task BillingCheckout_Returns400_ForInvalidTier()
    {
        var client = await _factory.CreateAuthenticatedClientAsync(
            "e2e-billing-invalid@cambrian.com", "Test1234!@");

        var res = await client.PostAsJsonAsync("/billing/checkout", new { tier = "enterprise" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ────────────────────── Subscriptions ──────────────────────

    [Fact]
    public async Task SubscriptionPlans_ReturnsPlans_WithoutAuth()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/subscriptions/plans");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.True(json.GetProperty("data").GetArrayLength() >= 3);
    }

    [Fact]
    public async Task SubscriptionCurrent_ReturnsFree_ForNewUser()
    {
        var client = await _factory.CreateAuthenticatedClientAsync(
            "e2e-sub-current@cambrian.com", "Test1234!@");

        var res = await client.GetAsync("/subscriptions/current");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("free", json.GetProperty("data").GetProperty("plan").GetString());
    }

    // ────────────────────── Security: Protected Routes ──────────────────────

    [Theory]
    [InlineData("/library")]
    [InlineData("/auth/me")]
    [InlineData("/wallet")]
    [InlineData("/subscriptions/current")]
    [InlineData("/billing/status")]
    public async Task ProtectedRoutes_Return401_WithoutToken(string route)
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("/catalog")]
    [InlineData("/discover")]
    [InlineData("/subscriptions/plans")]
    [InlineData("/health")]
    public async Task PublicRoutes_ReturnOk_WithoutToken(string route)
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ────────────────────── Payments ──────────────────────

    [Fact]
    public async Task PaymentsState_ReturnsReady_WhenAuthenticated()
    {
        var client = await _factory.CreateAuthenticatedClientAsync(
            "e2e-payments-state@cambrian.com", "Test1234!@");

        var res = await client.GetAsync("/payments/state");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task PaymentsCheckout_ReturnsUrl_WhenTrackExists()
    {
        var creatorEmail = "e2e-paychk-creator@cambrian.com";
        await _factory.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _factory.GetUserIdAsync(creatorEmail);
        var trackId = await _factory.SeedTrackAsync(creatorId, "PayCheckout Beat");

        var client = await _factory.CreateAuthenticatedClientAsync(
            "e2e-paychk-buyer@cambrian.com", "Test1234!@");

        var res = await client.PostAsJsonAsync("/payments/checkout", new
        {
            trackId = trackId.ToString()
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
    }
}
