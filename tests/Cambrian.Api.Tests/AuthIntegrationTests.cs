using System.Net;
using Cambrian.Api.Tests.Fixtures;

namespace Cambrian.Api.Tests;

public sealed class AuthIntegrationTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public AuthIntegrationTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Auth_RoundTrip_Works()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(
            email: $"roundtrip-{Guid.NewGuid():N}@test.com",
            password: "Test1234!@");

        var response = await client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}