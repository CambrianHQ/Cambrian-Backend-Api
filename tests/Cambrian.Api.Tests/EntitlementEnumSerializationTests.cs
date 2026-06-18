using System.Net;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// The entitlement contract must serialize its enums as stable string names
/// (e.g. "Download"), not raw integers, so the frontend isn't coupled to ordinal values.
/// </summary>
public sealed class EntitlementEnumSerializationTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public EntitlementEnumSerializationTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MeEntitlements_SerializeEnumsAsStrings()
    {
        var email = $"ent-enum-{Guid.NewGuid():N}@cambrian.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        using (var scope = _fixture.Services.CreateScope())
        {
            var entitlements = scope.ServiceProvider.GetRequiredService<IEntitlementService>();
            await entitlements.GrantAsync(
                userId, EntitlementResourceType.Track, Guid.NewGuid().ToString(),
                EntitlementAccessLevel.Download, EntitlementSourceType.Purchase);
        }

        var res = await client.GetAsync("/api/entitlements/me");
        var body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"accessLevel\":\"Download\"", body);
        Assert.Contains("\"resourceType\":\"Track\"", body);
        Assert.Contains("\"sourceType\":\"Purchase\"", body);
        Assert.DoesNotContain("\"accessLevel\":2", body); // never the raw ordinal
    }
}
