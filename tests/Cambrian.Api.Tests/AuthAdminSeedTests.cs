using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;

namespace Cambrian.Api.Tests;

public sealed class AuthAdminSessionTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public AuthAdminSessionTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AdminWithoutUsername_DoesNotRequireUsernameSetup_OnLoginOrMe()
    {
        var email = $"session-admin-{Guid.NewGuid():N}@test.cambrian";
        const string password = "AdminSeed123!@";

        await _fixture.RegisterUserAsync(email, password);
        await _fixture.SetUserRoleAsync(email, "Admin");

        var client = _fixture.CreateClient();

        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            email,
            password
        });
        login.EnsureSuccessStatusCode();

        var loginJson = await login.Content.ReadFromJsonAsync<JsonElement>();
        var loginData = loginJson.GetProperty("data");

        Assert.Equal("Admin", loginData.GetProperty("role").GetString());
        Assert.False(loginData.GetProperty("needsUsername").GetBoolean());
        Assert.False(loginData.GetProperty("requiresUsernameSetup").GetBoolean());
        Assert.Equal(JsonValueKind.Null, loginData.GetProperty("user").GetProperty("username").ValueKind);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            loginData.GetProperty("token").GetString());

        var me = await client.GetAsync("/auth/me");
        me.EnsureSuccessStatusCode();

        var meJson = await me.Content.ReadFromJsonAsync<JsonElement>();
        var meData = meJson.GetProperty("data");

        Assert.Equal("Admin", meData.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, meData.GetProperty("username").ValueKind);
        Assert.False(meData.GetProperty("needsUsername").GetBoolean());
        Assert.False(meData.GetProperty("requiresUsernameSetup").GetBoolean());
        Assert.False(meData.GetProperty("canChangeUsername").GetBoolean());
    }
}
