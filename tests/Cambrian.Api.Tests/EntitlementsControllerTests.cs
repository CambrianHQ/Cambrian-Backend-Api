using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

public sealed class EntitlementsControllerTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public EntitlementsControllerTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Grant_AsAdmin_CreatesRow_AndAccessCheckReturnsTrue()
    {
        var adminEmail = $"ent-admin-{Guid.NewGuid():N}@test.com";
        var userEmail = $"ent-user-{Guid.NewGuid():N}@test.com";

        var adminClient = await _fixture.CreateRoleClientAsync(adminEmail, "Test1234!@", "Admin");
        var userClient = await _fixture.CreateAuthenticatedClientAsync(userEmail, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(userEmail);

        var resourceId = Guid.NewGuid().ToString();

        var grantResp = await adminClient.PostAsJsonAsync("/api/entitlements/grant", new
        {
            userId,
            resourceType = (int)EntitlementResourceType.Track,
            resourceId,
            accessLevel = (int)EntitlementAccessLevel.Download,
            sourceType = (int)EntitlementSourceType.Admin,
            sourceId = "support-ticket-42",
        });

        Assert.Equal(HttpStatusCode.Created, grantResp.StatusCode);

        var accessResp = await userClient.GetAsync(
            $"/api/entitlements/access?resourceType={(int)EntitlementResourceType.Track}" +
            $"&resourceId={resourceId}" +
            $"&level={(int)EntitlementAccessLevel.Download}");

        Assert.Equal(HttpStatusCode.OK, accessResp.StatusCode);
        var json = await accessResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("data").GetProperty("hasAccess").GetBoolean());
    }

    [Fact]
    public async Task Grant_AsNonAdmin_ReturnsForbidden()
    {
        var userEmail = $"ent-nonadmin-{Guid.NewGuid():N}@test.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(userEmail, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(userEmail);

        var resp = await client.PostAsJsonAsync("/api/entitlements/grant", new
        {
            userId,
            resourceType = (int)EntitlementResourceType.Track,
            resourceId = Guid.NewGuid().ToString(),
            accessLevel = (int)EntitlementAccessLevel.Download,
            sourceType = (int)EntitlementSourceType.Admin,
        });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Grant_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _fixture.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/entitlements/grant", new
        {
            userId = "user-1",
            resourceType = (int)EntitlementResourceType.Track,
            resourceId = Guid.NewGuid().ToString(),
            accessLevel = (int)EntitlementAccessLevel.Download,
            sourceType = (int)EntitlementSourceType.Admin,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Grant_WithPastExpiresAt_ReturnsBadRequest()
    {
        var adminClient = await _fixture.CreateRoleClientAsync(
            $"ent-pastexp-admin-{Guid.NewGuid():N}@test.com", "Test1234!@", "Admin");
        var userEmail = $"ent-pastexp-user-{Guid.NewGuid():N}@test.com";
        await _fixture.CreateAuthenticatedClientAsync(userEmail, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(userEmail);

        var resp = await adminClient.PostAsJsonAsync("/api/entitlements/grant", new
        {
            userId,
            resourceType = (int)EntitlementResourceType.Track,
            resourceId = Guid.NewGuid().ToString(),
            accessLevel = (int)EntitlementAccessLevel.Download,
            sourceType = (int)EntitlementSourceType.Admin,
            expiresAt = DateTime.UtcNow.AddDays(-1),
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Access_WithoutGrant_ReturnsFalse()
    {
        var userEmail = $"ent-noaccess-{Guid.NewGuid():N}@test.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(userEmail, "Test1234!@");

        var resp = await client.GetAsync(
            $"/api/entitlements/access?resourceType={(int)EntitlementResourceType.Track}" +
            $"&resourceId={Guid.NewGuid()}" +
            $"&level={(int)EntitlementAccessLevel.Stream}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("data").GetProperty("hasAccess").GetBoolean());
    }

    [Fact]
    public async Task Access_HigherGrantSatisfiesLowerRequirement()
    {
        // License (3) grant should satisfy Stream (1) and Download (2) checks.
        var adminClient = await _fixture.CreateRoleClientAsync(
            $"ent-rank-admin-{Guid.NewGuid():N}@test.com", "Test1234!@", "Admin");
        var userEmail = $"ent-rank-user-{Guid.NewGuid():N}@test.com";
        var userClient = await _fixture.CreateAuthenticatedClientAsync(userEmail, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(userEmail);
        var resourceId = Guid.NewGuid().ToString();

        var grantResp = await adminClient.PostAsJsonAsync("/api/entitlements/grant", new
        {
            userId,
            resourceType = (int)EntitlementResourceType.Track,
            resourceId,
            accessLevel = (int)EntitlementAccessLevel.License,
            sourceType = (int)EntitlementSourceType.Admin,
        });
        grantResp.EnsureSuccessStatusCode();

        foreach (var lvl in new[] { EntitlementAccessLevel.Stream, EntitlementAccessLevel.Download, EntitlementAccessLevel.License })
        {
            var resp = await userClient.GetAsync(
                $"/api/entitlements/access?resourceType={(int)EntitlementResourceType.Track}" +
                $"&resourceId={resourceId}" +
                $"&level={(int)lvl}");
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("data").GetProperty("hasAccess").GetBoolean(),
                $"License grant should satisfy {lvl} check.");
        }
    }

    [Fact]
    public async Task Access_LowerGrantDoesNotSatisfyHigherRequirement()
    {
        // Stream (1) grant should NOT satisfy Download (2) check.
        var adminClient = await _fixture.CreateRoleClientAsync(
            $"ent-lowrank-admin-{Guid.NewGuid():N}@test.com", "Test1234!@", "Admin");
        var userEmail = $"ent-lowrank-user-{Guid.NewGuid():N}@test.com";
        var userClient = await _fixture.CreateAuthenticatedClientAsync(userEmail, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(userEmail);
        var resourceId = Guid.NewGuid().ToString();

        var grantResp = await adminClient.PostAsJsonAsync("/api/entitlements/grant", new
        {
            userId,
            resourceType = (int)EntitlementResourceType.Track,
            resourceId,
            accessLevel = (int)EntitlementAccessLevel.Stream,
            sourceType = (int)EntitlementSourceType.Admin,
        });
        grantResp.EnsureSuccessStatusCode();

        var resp = await userClient.GetAsync(
            $"/api/entitlements/access?resourceType={(int)EntitlementResourceType.Track}" +
            $"&resourceId={resourceId}" +
            $"&level={(int)EntitlementAccessLevel.Download}");

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("data").GetProperty("hasAccess").GetBoolean());
    }

    [Fact]
    public async Task Revoke_AsAdmin_SoftDeletesRow_AndAccessCheckReturnsFalse()
    {
        var adminClient = await _fixture.CreateRoleClientAsync(
            $"ent-revoke-admin-{Guid.NewGuid():N}@test.com", "Test1234!@", "Admin");
        var userEmail = $"ent-revoke-user-{Guid.NewGuid():N}@test.com";
        var userClient = await _fixture.CreateAuthenticatedClientAsync(userEmail, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(userEmail);
        var resourceId = Guid.NewGuid().ToString();

        var grantResp = await adminClient.PostAsJsonAsync("/api/entitlements/grant", new
        {
            userId,
            resourceType = (int)EntitlementResourceType.Track,
            resourceId,
            accessLevel = (int)EntitlementAccessLevel.Download,
            sourceType = (int)EntitlementSourceType.Admin,
        });
        grantResp.EnsureSuccessStatusCode();
        var grantJson = await grantResp.Content.ReadFromJsonAsync<JsonElement>();
        var entId = grantJson.GetProperty("data").GetProperty("id").GetGuid();

        var revokeReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/entitlements/{entId}")
        {
            Content = JsonContent.Create(new { reason = "refund processed" }),
        };
        var revokeResp = await adminClient.SendAsync(revokeReq);
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);

        var accessResp = await userClient.GetAsync(
            $"/api/entitlements/access?resourceType={(int)EntitlementResourceType.Track}" +
            $"&resourceId={resourceId}" +
            $"&level={(int)EntitlementAccessLevel.Download}");
        accessResp.EnsureSuccessStatusCode();
        var accessJson = await accessResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(accessJson.GetProperty("data").GetProperty("hasAccess").GetBoolean());

        // Audit preserved: row still exists in DB with RevokedAt set.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var row = await db.Entitlements.AsNoTracking().SingleAsync(e => e.Id == entId);
        Assert.NotNull(row.RevokedAt);
        Assert.Equal("refund processed", row.RevokedReason);
    }

    [Fact]
    public async Task Revoke_UnknownId_ReturnsNotFound()
    {
        var adminClient = await _fixture.CreateRoleClientAsync(
            $"ent-rev404-admin-{Guid.NewGuid():N}@test.com", "Test1234!@", "Admin");

        var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/entitlements/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new { reason = "because" }),
        };
        var resp = await adminClient.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Revoke_AsNonAdmin_ReturnsForbidden()
    {
        var userEmail = $"ent-rev-forbid-{Guid.NewGuid():N}@test.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(userEmail, "Test1234!@");

        var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/entitlements/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new { reason = "nope" }),
        };
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Access_ExpiredEntitlementReturnsFalse()
    {
        // Grant directly in the DB with a past ExpiresAt — the grant endpoint
        // refuses to insert a past expiry, so we seed through the repository.
        var userEmail = $"ent-expired-{Guid.NewGuid():N}@test.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(userEmail, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(userEmail);
        var resourceId = Guid.NewGuid().ToString();

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            db.Entitlements.Add(new Entitlement
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ResourceType = EntitlementResourceType.Track,
                ResourceId = resourceId,
                AccessLevel = EntitlementAccessLevel.Download,
                SourceType = EntitlementSourceType.Admin,
                GrantedAt = DateTime.UtcNow.AddDays(-30),
                ExpiresAt = DateTime.UtcNow.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync(
            $"/api/entitlements/access?resourceType={(int)EntitlementResourceType.Track}" +
            $"&resourceId={resourceId}" +
            $"&level={(int)EntitlementAccessLevel.Download}");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("data").GetProperty("hasAccess").GetBoolean());
    }

    [Fact]
    public async Task Mine_ReturnsCallersEntitlements_AndHidesRevokedByDefault()
    {
        var adminClient = await _fixture.CreateRoleClientAsync(
            $"ent-mine-admin-{Guid.NewGuid():N}@test.com", "Test1234!@", "Admin");
        var userEmail = $"ent-mine-user-{Guid.NewGuid():N}@test.com";
        var userClient = await _fixture.CreateAuthenticatedClientAsync(userEmail, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(userEmail);

        var activeResourceId = Guid.NewGuid().ToString();
        var revokedResourceId = Guid.NewGuid().ToString();

        var activeGrant = await adminClient.PostAsJsonAsync("/api/entitlements/grant", new
        {
            userId,
            resourceType = (int)EntitlementResourceType.Track,
            resourceId = activeResourceId,
            accessLevel = (int)EntitlementAccessLevel.Stream,
            sourceType = (int)EntitlementSourceType.Admin,
        });
        activeGrant.EnsureSuccessStatusCode();

        var revokedGrant = await adminClient.PostAsJsonAsync("/api/entitlements/grant", new
        {
            userId,
            resourceType = (int)EntitlementResourceType.Track,
            resourceId = revokedResourceId,
            accessLevel = (int)EntitlementAccessLevel.Download,
            sourceType = (int)EntitlementSourceType.Admin,
        });
        revokedGrant.EnsureSuccessStatusCode();
        var revokedJson = await revokedGrant.Content.ReadFromJsonAsync<JsonElement>();
        var revokedId = revokedJson.GetProperty("data").GetProperty("id").GetGuid();

        var revokeReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/entitlements/{revokedId}")
        {
            Content = JsonContent.Create(new { reason = "test revoke" }),
        };
        (await adminClient.SendAsync(revokeReq)).EnsureSuccessStatusCode();

        var defaultResp = await userClient.GetAsync("/api/entitlements/me");
        defaultResp.EnsureSuccessStatusCode();
        var defaultJson = await defaultResp.Content.ReadFromJsonAsync<JsonElement>();
        var defaultItems = defaultJson.GetProperty("data").EnumerateArray().ToList();
        Assert.Single(defaultItems);
        Assert.Equal(activeResourceId, defaultItems[0].GetProperty("resourceId").GetString());

        var allResp = await userClient.GetAsync("/api/entitlements/me?includeRevoked=true");
        allResp.EnsureSuccessStatusCode();
        var allJson = await allResp.Content.ReadFromJsonAsync<JsonElement>();
        var allItems = allJson.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(2, allItems.Count);
    }

    [Fact]
    public async Task Mine_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _fixture.CreateClient();
        var resp = await client.GetAsync("/api/entitlements/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
