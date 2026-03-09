using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Cambrian.Api.Tests;

/// <summary>
/// End-to-end authentication flow tests verifying the complete lifecycle:
/// register → login → access protected resources → token validation.
/// Tests run through the full HTTP pipeline including JWT middleware.
/// </summary>
public sealed class AuthFlowIntegrationTests : IClassFixture<CambrianWebApplicationFactory>
{
    private readonly CambrianWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public AuthFlowIntegrationTests(CambrianWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── Full register → login → /me flow ──

    [Fact]
    public async Task FullFlow_Register_Login_AccessProfile()
    {
        var email = $"flow-{Guid.NewGuid():N}@test.com";
        var password = "StrongP@ss1!";

        // Step 1: Register
        var registerResponse = await _client.PostAsJsonAsync("/auth/register", new { email, password });
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        var registerBody = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var registerToken = registerBody.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(registerToken));

        // Step 2: Login with the same credentials
        var loginResponse = await _client.PostAsJsonAsync("/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var loginBody = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var loginToken = loginBody.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(loginToken));

        // Tokens should be different (each has a unique JTI)
        Assert.NotEqual(registerToken, loginToken);

        // Step 3: Access /auth/me with the login token
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginToken);
        var meResponse = await _client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        var meBody = await meResponse.Content.ReadFromJsonAsync<JsonElement>();
        var userEmail = meBody.GetProperty("user").GetProperty("email").GetString();
        Assert.Equal(email, userEmail);
    }

    // ── Expired/malformed token rejection ──

    [Fact]
    public async Task MalformedToken_ReturnsUnauthorized()
    {
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "this-is-not-a-valid-jwt");

        var response = await _client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EmptyBearerToken_ReturnsUnauthorized()
    {
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "");

        var response = await _client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Token from wrong signing key ──

    [Fact]
    public async Task TokenSignedWithWrongKey_ReturnsUnauthorized()
    {
        var wrongKeyToken = GenerateTokenWithKey(
            "completely-different-secret-key-min-32-chars!!");

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", wrongKeyToken);

        var response = await _client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Register + access protected endpoint in one flow ──

    [Fact]
    public async Task RegisterToken_CanAccessProtectedEndpoint()
    {
        var email = $"regprotect-{Guid.NewGuid():N}@test.com";

        var registerResponse = await _client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "StrongP@ss1!"
        });
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        var body = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var libraryResponse = await _client.GetAsync("/library");
        Assert.Equal(HttpStatusCode.OK, libraryResponse.StatusCode);
    }

    // ── Register returns correct tier ──

    [Fact]
    public async Task Register_ReturnsFreeTier()
    {
        var email = $"tier-{Guid.NewGuid():N}@test.com";

        var response = await _client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "StrongP@ss1!"
        });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tier = body.GetProperty("tier").GetString();
        Assert.Equal("free", tier);
    }

    // ── Login preserves user ID across sessions ──

    [Fact]
    public async Task Login_ReturnsSameUserId_AcrossSessions()
    {
        var email = $"sameid-{Guid.NewGuid():N}@test.com";
        var password = "StrongP@ss1!";

        var reg = await _client.PostAsJsonAsync("/auth/register", new { email, password });
        var regBody = await reg.Content.ReadFromJsonAsync<JsonElement>();
        var regUserId = regBody.GetProperty("user").GetProperty("id").GetString();

        var login = await _client.PostAsJsonAsync("/auth/login", new { email, password });
        var loginBody = await login.Content.ReadFromJsonAsync<JsonElement>();
        var loginUserId = loginBody.GetProperty("user").GetProperty("id").GetString();

        Assert.Equal(regUserId, loginUserId);
    }

    // ── Register with display name ──

    [Fact]
    public async Task Register_WithDisplayName_Succeeds()
    {
        var response = await _client.PostAsJsonAsync("/auth/register", new
        {
            email = $"display-{Guid.NewGuid():N}@test.com",
            password = "StrongP@ss1!",
            displayName = "Test Creator Name"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ── Password validation at HTTP level ──

    [Fact]
    public async Task Register_MissingEmail_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/auth/register", new
        {
            email = "",
            password = "StrongP@ss1!"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_InvalidEmailFormat_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/auth/register", new
        {
            email = "not-an-email",
            password = "StrongP@ss1!"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Multiple endpoints require the same valid auth ──

    [Fact]
    public async Task ValidToken_CanAccessMultipleProtectedEndpoints()
    {
        var email = $"multi-{Guid.NewGuid():N}@test.com";

        var reg = await _client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "StrongP@ss1!"
        });
        var body = await reg.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var meResponse = await _client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        var libraryResponse = await _client.GetAsync("/library");
        Assert.Equal(HttpStatusCode.OK, libraryResponse.StatusCode);

        var walletResponse = await _client.GetAsync("/wallet");
        Assert.Equal(HttpStatusCode.OK, walletResponse.StatusCode);

        var subscriptionResponse = await _client.GetAsync("/subscriptions/current");
        Assert.Equal(HttpStatusCode.OK, subscriptionResponse.StatusCode);

        var billingResponse = await _client.GetAsync("/billing/status");
        Assert.Equal(HttpStatusCode.OK, billingResponse.StatusCode);
    }

    // ── Verify password requirements through full pipeline ──

    [Theory]
    [InlineData("short1!")]          // Too short
    [InlineData("nouppercase1!")]    // No uppercase
    [InlineData("NOLOWERCASE1!")]    // No lowercase
    [InlineData("NoDigitsHere!")]    // No digit
    [InlineData("NoSpecial1234")]    // No special char
    public async Task Register_PasswordPolicy_RejectedByIdentity(string weakPassword)
    {
        var response = await _client.PostAsJsonAsync("/auth/register", new
        {
            email = $"pw-{Guid.NewGuid():N}@test.com",
            password = weakPassword
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Sequential duplicate registration attempt ──

    [Fact]
    public async Task DuplicateRegistration_SameEmail_SecondFails()
    {
        var email = $"dupseq-{Guid.NewGuid():N}@test.com";
        var password = "StrongP@ss1!";

        var first = await _client.PostAsJsonAsync("/auth/register", new { email, password });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _client.PostAsJsonAsync("/auth/register", new { email, password });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("already", body, StringComparison.OrdinalIgnoreCase);
    }

    private static string GenerateTokenWithKey(string key)
    {
        var securityKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes(key));
        var creds = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            securityKey, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: CambrianWebApplicationFactory.TestIssuer,
            audience: CambrianWebApplicationFactory.TestAudience,
            claims: new[]
            {
                new System.Security.Claims.Claim("sub", "fake-user-id"),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "fake-user-id")
            },
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }
}
