using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// Full HTTP pipeline integration tests using WebApplicationFactory.
/// These tests verify endpoint behavior through the entire middleware stack
/// (routing, auth, model binding, exception handling, serialization).
/// </summary>
public sealed class EndpointIntegrationTests : IClassFixture<CambrianWebApplicationFactory>, IAsyncLifetime
{
    private readonly CambrianWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private string _authToken = "";
    private string _userId = "";

    public EndpointIntegrationTests(CambrianWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        await db.Database.EnsureCreatedAsync();

        _authToken = await _factory.CreateTestUserAndGetTokenAsync(
            $"endpoint-{Guid.NewGuid():N}@test.com", "StrongP@ss1!");

        var handler = new JwtSecurityTokenHandlerHelper();
        _userId = handler.GetUserId(_authToken);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void Authenticate() =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);

    private void ClearAuth() =>
        _client.DefaultRequestHeaders.Authorization = null;

    // ── Catalog: public endpoints ──

    [Fact]
    public async Task GET_Catalog_ReturnsOk_WithoutAuth()
    {
        ClearAuth();
        var response = await _client.GetAsync("/catalog");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("success", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GET_Discover_ReturnsOk_WithPagination()
    {
        ClearAuth();
        var response = await _client.GetAsync("/discover?page=1&pageSize=5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GET_Trending_ReturnsOk()
    {
        ClearAuth();
        var response = await _client.GetAsync("/trending?page=1&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GET_Track_InvalidGuid_ReturnsBadRequest()
    {
        ClearAuth();
        var response = await _client.GetAsync("/tracks/not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("GUID", body);
    }

    [Fact]
    public async Task GET_Track_NonexistentGuid_ReturnsNotFound()
    {
        ClearAuth();
        var response = await _client.GetAsync($"/tracks/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GET_Track_ValidId_ReturnsTrack()
    {
        var trackId = Guid.NewGuid();
        await _factory.SeedTrackAsync(trackId, "creator-catalog-1", "Integration Beat");

        ClearAuth();
        var response = await _client.GetAsync($"/tracks/{trackId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Integration Beat", body);
    }

    [Fact]
    public async Task GET_Tracks_ListAll_ReturnsOk()
    {
        ClearAuth();
        var response = await _client.GetAsync("/tracks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Auth: register ──

    [Fact]
    public async Task POST_Register_ReturnsCreated_WithToken()
    {
        ClearAuth();
        var response = await _client.PostAsJsonAsync("/auth/register", new
        {
            email = $"newuser-{Guid.NewGuid():N}@test.com",
            password = "StrongP@ss1!"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("token", body);
    }

    [Fact]
    public async Task POST_Register_DuplicateEmail_ReturnsBadRequest()
    {
        ClearAuth();
        var email = $"dup-{Guid.NewGuid():N}@test.com";

        await _client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "StrongP@ss1!"
        });

        var response = await _client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "StrongP@ss1!"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_Register_WeakPassword_ReturnsBadRequest()
    {
        ClearAuth();
        var response = await _client.PostAsJsonAsync("/auth/register", new
        {
            email = $"weak-{Guid.NewGuid():N}@test.com",
            password = "123"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Auth: login ──

    [Fact]
    public async Task POST_Login_ValidCredentials_ReturnsOk()
    {
        ClearAuth();
        var email = $"login-{Guid.NewGuid():N}@test.com";
        var password = "StrongP@ss1!";

        await _client.PostAsJsonAsync("/auth/register", new { email, password });

        var response = await _client.PostAsJsonAsync("/auth/login", new { email, password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("token", body);
    }

    [Fact]
    public async Task POST_Login_InvalidCredentials_ReturnsForbidden()
    {
        ClearAuth();
        var response = await _client.PostAsJsonAsync("/auth/login", new
        {
            email = "nobody@test.com",
            password = "WrongPass1!"
        });

        // ExceptionMiddleware maps UnauthorizedAccessException → 403
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Auth: /me ──

    [Fact]
    public async Task GET_Me_WithToken_ReturnsProfile()
    {
        Authenticate();
        var response = await _client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("user", body);
        Assert.Contains("token", body);
    }

    [Fact]
    public async Task GET_Me_WithoutToken_ReturnsUnauthorized()
    {
        ClearAuth();
        var response = await _client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Auth: logout ──

    [Fact]
    public async Task POST_Logout_WithToken_ReturnsOk()
    {
        Authenticate();
        var response = await _client.PostAsync("/auth/logout", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Logged out", body);
    }

    [Fact]
    public async Task POST_Logout_WithoutToken_ReturnsUnauthorized()
    {
        ClearAuth();
        var response = await _client.PostAsync("/auth/logout", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Auth: password recovery ──

    [Fact]
    public async Task POST_ForgotPassword_ReturnsOk_EvenForUnknownEmail()
    {
        ClearAuth();
        var response = await _client.PostAsJsonAsync("/auth/forgot-password", new
        {
            email = "unknown@test.com"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Library: requires auth ──

    [Fact]
    public async Task GET_Library_WithoutAuth_ReturnsUnauthorized()
    {
        ClearAuth();
        var response = await _client.GetAsync("/library");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_Library_WithAuth_ReturnsOk()
    {
        Authenticate();
        var response = await _client.GetAsync("/library");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task POST_Library_WithAuth_SavesTrack()
    {
        var trackId = Guid.NewGuid();
        await _factory.SeedTrackAsync(trackId, "creator-lib-1");

        Authenticate();
        var response = await _client.PostAsJsonAsync("/library", new
        {
            trackId = trackId.ToString(),
            title = "Saved Beat",
            artist = "Creator"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task DELETE_Library_InvalidGuid_ReturnsBadRequest()
    {
        Authenticate();
        var response = await _client.DeleteAsync("/library/not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GET_PurchasedTrackIds_WithAuth_ReturnsOk()
    {
        Authenticate();
        var response = await _client.GetAsync("/library/purchased-track-ids");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Download: requires auth and ownership ──

    [Fact]
    public async Task GET_Download_WithoutAuth_ReturnsUnauthorized()
    {
        ClearAuth();
        var response = await _client.GetAsync($"/download/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_Download_NotOwned_ReturnsForbidden()
    {
        var trackId = Guid.NewGuid();
        await _factory.SeedTrackAsync(trackId, "creator-dl-1");

        Authenticate();
        var response = await _client.GetAsync($"/download/{trackId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_Download_InvalidGuid_ReturnsBadRequest()
    {
        Authenticate();
        var response = await _client.GetAsync("/download/bad-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GET_Download_Owned_ReturnsSignedUrl()
    {
        var trackId = Guid.NewGuid();
        await _factory.SeedTrackAsync(trackId, "creator-dl-2");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        db.Library.Add(new LibraryItem
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            TrackId = trackId,
            Title = "Owned Beat"
        });
        await db.SaveChangesAsync();

        Authenticate();
        var response = await _client.GetAsync($"/download/{trackId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("url", body);
    }

    // ── Stream: mixed auth ──

    [Fact]
    public async Task GET_StreamList_WithoutAuth_ReturnsOk()
    {
        ClearAuth();
        var response = await _client.GetAsync("/stream");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GET_Stream_InvalidGuid_ReturnsBadRequest()
    {
        ClearAuth();
        var response = await _client.GetAsync("/stream/not-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GET_Stream_ValidTrack_ReturnsSignedUrl()
    {
        var trackId = Guid.NewGuid();
        await _factory.SeedTrackAsync(trackId, "creator-stream-1", audioUrl: "/audio/stream.mp3");

        ClearAuth();
        var response = await _client.GetAsync($"/stream/{trackId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("streamUrl", body);
    }

    [Fact]
    public async Task POST_StreamStart_WithoutAuth_ReturnsUnauthorized()
    {
        ClearAuth();
        var response = await _client.PostAsJsonAsync("/stream/start", new { trackId = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task POST_StreamStart_WithAuth_ReturnsOk()
    {
        Authenticate();
        var response = await _client.PostAsJsonAsync("/stream/start", new { trackId = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("started", body);
    }

    // ── Subscriptions ──

    [Fact]
    public async Task GET_SubscriptionPlans_WithoutAuth_ReturnsOk()
    {
        ClearAuth();
        var response = await _client.GetAsync("/subscriptions/plans");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Free", body);
        Assert.Contains("Paid", body);
        Assert.Contains("Creator", body);
    }

    [Fact]
    public async Task GET_SubscriptionCurrent_WithoutAuth_ReturnsUnauthorized()
    {
        ClearAuth();
        var response = await _client.GetAsync("/subscriptions/current");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_SubscriptionCurrent_WithAuth_ReturnsFree()
    {
        Authenticate();
        var response = await _client.GetAsync("/subscriptions/current");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("free", body);
    }

    [Fact]
    public async Task POST_SubscriptionUpdate_WithAuth_UpgradesToPaid()
    {
        Authenticate();
        var response = await _client.PostAsJsonAsync("/subscriptions/update", new { plan = "paid" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("paid", body);
    }

    // ── Billing ──

    [Fact]
    public async Task POST_BillingCheckout_WithoutAuth_ReturnsUnauthorized()
    {
        ClearAuth();
        var response = await _client.PostAsJsonAsync("/billing/checkout", new { tier = "paid" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task POST_BillingCheckout_InvalidTier_ReturnsBadRequest()
    {
        Authenticate();
        var response = await _client.PostAsJsonAsync("/billing/checkout", new { tier = "invalid" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_BillingCheckout_ValidTier_ReturnsUrl()
    {
        Authenticate();
        var response = await _client.PostAsJsonAsync("/billing/checkout", new { tier = "paid" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("url", body);
    }

    [Fact]
    public async Task GET_BillingStatus_WithAuth_ReturnsStatus()
    {
        Authenticate();
        var response = await _client.GetAsync("/billing/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("tier", body);
    }

    // ── Wallet ──

    [Fact]
    public async Task GET_Wallet_WithoutAuth_ReturnsUnauthorized()
    {
        ClearAuth();
        var response = await _client.GetAsync("/wallet");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_Wallet_WithAuth_ReturnsBalance()
    {
        Authenticate();
        var response = await _client.GetAsync("/wallet");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("balanceCents", body);
    }

    [Fact]
    public async Task POST_WalletWithdraw_InsufficientBalance_ReturnsBadRequest()
    {
        Authenticate();
        var response = await _client.PostAsJsonAsync("/wallet/withdraw", new { amount = 999999.0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Insufficient", body);
    }

    [Fact]
    public async Task GET_WalletHistory_WithAuth_ReturnsOk()
    {
        Authenticate();
        var response = await _client.GetAsync("/wallet/history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Payments ──

    [Fact]
    public async Task POST_PaymentsCheckout_WithoutAuth_ReturnsUnauthorized()
    {
        ClearAuth();
        var response = await _client.PostAsJsonAsync("/payments/checkout", new { trackId = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_PaymentsResult_Anonymous_ReturnsOk()
    {
        ClearAuth();
        var response = await _client.GetAsync("/payments/result?status=success&trackId=" + Guid.NewGuid());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Checkout ──

    [Fact]
    public async Task POST_Checkout_WithoutAuth_ReturnsUnauthorized()
    {
        ClearAuth();
        var response = await _client.PostAsJsonAsync("/checkout", new
        {
            trackId = Guid.NewGuid().ToString(),
            licenseType = "non-exclusive"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Webhook ──

    [Fact]
    public async Task POST_WebhookStripe_ValidEvent_ReturnsOk()
    {
        ClearAuth();
        var payload = """{"type":"payment_intent.succeeded","data":{"object":{}}}""";
        var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/webhook/stripe", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task POST_WebhookStripe_MalformedBody_ReturnsNon500()
    {
        ClearAuth();
        var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/webhook/stripe", content);

        // Empty JSON missing "type" key causes KeyNotFoundException → 404 via ExceptionMiddleware
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── Health endpoints ──

    [Fact]
    public async Task GET_AuthHealth_ReturnsOk()
    {
        ClearAuth();
        var response = await _client.GetAsync("/auth/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GET_CsrfToken_ReturnsToken()
    {
        ClearAuth();
        var response = await _client.GetAsync("/auth/csrf-token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("token", body);
    }

    // ── Settings (protected) ──

    [Fact]
    public async Task GET_SettingsProfile_WithoutAuth_ReturnsUnauthorized()
    {
        ClearAuth();
        var response = await _client.GetAsync("/settings/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_SettingsProfile_WithAuth_ReturnsOk()
    {
        Authenticate();
        var response = await _client.GetAsync("/settings/profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task POST_SettingsPassword_WithAuth_ReturnsOk()
    {
        Authenticate();
        var response = await _client.PostAsync("/settings/password", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

internal class JwtSecurityTokenHandlerHelper
{
    public string GetUserId(string token)
    {
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        return jwt.Claims.First(c => c.Type == "sub" || c.Type == System.Security.Claims.ClaimTypes.NameIdentifier).Value;
    }
}
