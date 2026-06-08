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
}
