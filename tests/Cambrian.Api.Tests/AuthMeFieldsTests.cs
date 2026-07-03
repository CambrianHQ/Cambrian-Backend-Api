using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// B3 regression: GET /auth/me must include hasPassword and googleLinked. The frontend gates the
/// Change Password / Change Email forms on these; when absent (undefined) an email/password user
/// could not change their password or email.
/// </summary>
public sealed class AuthMeFieldsTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public AuthMeFieldsTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Me_Includes_HasPassword_And_GoogleLinked()
    {
        var email = $"me-fields-{Guid.NewGuid():N}@test.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(email, "Test1234!@");

        var res = await client.GetAsync("/auth/me");
        res.EnsureSuccessStatusCode();
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        Assert.True(data.TryGetProperty("hasPassword", out var hasPassword), "Missing 'hasPassword'");
        Assert.True(hasPassword.GetBoolean(), "Email/password user should have hasPassword=true");

        Assert.True(data.TryGetProperty("googleLinked", out var googleLinked), "Missing 'googleLinked'");
        Assert.False(googleLinked.GetBoolean(), "User registered without Google should have googleLinked=false");
    }

    /// <summary>
    /// Creator-activation regression: /auth/me must expose emailVerified so the frontend can
    /// surface a verification banner BEFORE the user hits the VerifiedEmail 403 wall at publish
    /// time, and createdAt so analytics can compute signup_date / minutes_from_signup.
    /// </summary>
    [Fact]
    public async Task Me_Includes_EmailVerified_And_CreatedAt_And_Tracks_Verification_State()
    {
        var email = $"me-verified-{Guid.NewGuid():N}@test.com";
        var client = await _fixture.CreateUnverifiedClientAsync(email, "Test1234!@");

        var res = await client.GetAsync("/auth/me");
        res.EnsureSuccessStatusCode();
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        Assert.True(data.TryGetProperty("emailVerified", out var emailVerified), "Missing 'emailVerified'");
        Assert.False(emailVerified.GetBoolean(), "Freshly registered user must report emailVerified=false");

        Assert.True(data.TryGetProperty("createdAt", out var createdAt), "Missing 'createdAt'");
        Assert.True(createdAt.TryGetDateTime(out var created), "'createdAt' must be an ISO timestamp");
        Assert.True(created > DateTime.UtcNow.AddMinutes(-5), "'createdAt' should be the recent signup time");

        // After verification, /auth/me reads the fresh DB state (not the stale JWT claim),
        // so a "I've verified" refetch flips the flag without a re-login.
        await _fixture.SetEmailVerifiedAsync(email, true);

        var res2 = await client.GetAsync("/auth/me");
        res2.EnsureSuccessStatusCode();
        var data2 = (await res2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(data2.GetProperty("emailVerified").GetBoolean(), "emailVerified must flip to true after verification");
    }
}
