using System.Net;
using System.Net.Http.Json;
using Cambrian.Api.Tests.Fixtures;

namespace Cambrian.Api.Tests.Security;

/// <summary>
/// Explicit role matrix for the highest-value routes so access policy drift is
/// caught quickly when attributes or onboarding filters change.
/// </summary>
public sealed class RoleAccessMatrixTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public RoleAccessMatrixTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Catalog_Is_Accessible_To_All_Roles()
    {
        using var anonymous = _fixture.CreateClient();
        using var user = await CreateUserClientAsync();
        using var creator = await CreateCreatorClientAsync();
        using var admin = await CreateAdminClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await creator.GetAsync("/catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/catalog")).StatusCode);
    }

    [Fact]
    public async Task Upload_Requires_Creator_Or_Admin_Access()
    {
        using var user = await CreateUserClientAsync();
        using var creator = await CreateCreatorClientAsync();
        using var admin = await CreateAdminClientAsync();

        var userResponse = await user.PostAsync("/upload", new MultipartFormDataContent());
        var creatorResponse = await creator.PostAsync("/upload", new MultipartFormDataContent());
        var adminResponse = await admin.PostAsync("/upload", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.Forbidden, userResponse.StatusCode);
        Assert.DoesNotContain(creatorResponse.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.DoesNotContain(adminResponse.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    [Fact]
    public async Task Library_Is_Accessible_To_Authenticated_Roles()
    {
        using var user = await CreateUserClientAsync();
        using var creator = await CreateCreatorClientAsync();
        using var admin = await CreateAdminClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/library")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await creator.GetAsync("/library")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/library")).StatusCode);
    }

    [Fact]
    public async Task AdminUsers_Is_Admin_Only()
    {
        using var user = await CreateUserClientAsync();
        using var creator = await CreateCreatorClientAsync();
        using var admin = await CreateAdminClientAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await creator.GetAsync("/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin/users")).StatusCode);
    }

    [Fact]
    public async Task PayoutHistory_Requires_Creator_Or_Admin_Access()
    {
        using var user = await CreateUserClientAsync();
        using var creator = await CreateCreatorClientAsync();
        using var admin = await CreateAdminClientAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/payouts/history")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await creator.GetAsync("/payouts/history")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/payouts/history")).StatusCode);
    }

    [Fact]
    public async Task SubscriptionsCurrent_Is_Accessible_To_Authenticated_Roles()
    {
        using var user = await CreateUserClientAsync();
        using var creator = await CreateCreatorClientAsync();
        using var admin = await CreateAdminClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/subscriptions/current")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await creator.GetAsync("/subscriptions/current")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/subscriptions/current")).StatusCode);
    }

    private Task<HttpClient> CreateUserClientAsync()
    {
        var seed = Guid.NewGuid().ToString("N");
        return _fixture.CreateRoleClientAsync(
            email: $"matrix-user-{seed}@cambrian.com",
            password: "Test1234!@",
            role: "User",
            username: $"matrix-user-{seed[..8]}");
    }

    private Task<HttpClient> CreateCreatorClientAsync()
    {
        var seed = Guid.NewGuid().ToString("N");
        return _fixture.CreateRoleClientAsync(
            email: $"matrix-creator-{seed}@cambrian.com",
            password: "Test1234!@",
            role: "Creator",
            username: $"creator{seed[..8]}");
    }

    private Task<HttpClient> CreateAdminClientAsync()
    {
        var seed = Guid.NewGuid().ToString("N");
        return _fixture.CreateRoleClientAsync(
            email: $"matrix-admin-{seed}@cambrian.com",
            password: "Test1234!@",
            role: "Admin");
    }
}
