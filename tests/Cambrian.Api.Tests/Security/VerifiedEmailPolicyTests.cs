using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;

namespace Cambrian.Api.Tests.Security;

public sealed class VerifiedEmailPolicyTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public VerifiedEmailPolicyTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Checkout_ReturnsStructured403_ForUnverifiedUser()
    {
        var client = _fixture.CreateClient();
        var email = $"unverified-checkout-{Guid.NewGuid():N}@test.com";
        const string password = "Test1234!@";

        var registerResponse = await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password,
            displayName = "Unverified User"
        });
        registerResponse.EnsureSuccessStatusCode();

        var payload = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var token = payload.GetProperty("data").GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/checkout", new
        {
            trackId = Guid.NewGuid().ToString(),
            licenseType = "non-exclusive"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("email_not_verified", body.GetProperty("error").GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetProperty("message").GetString()));
    }
}
