using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// Contract shape tests for CreatorProfile endpoints.
/// Ensures the API response shape matches the agreed-upon DTO contract
/// so frontend and backend never drift.
/// </summary>
[Trait("Category", "Critical")]
public sealed class CreatorProfileContractTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public CreatorProfileContractTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetMyProfile_Unauthenticated_Returns401()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/creator-profile/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task GetBySlug_NonExistent_Returns404()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/creator-profile/nonexistent-slug-xyz");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task UpsertProfile_ReturnsExpectedShape()
    {
        // Register a creator-tier user
        var email = $"creator-contract-{Guid.NewGuid():N}@test.com";
        var password = "Test1234!@";
        var client = await CreateCreatorClientAsync(email, password);

        // Upsert profile
        var slug = $"test-{Guid.NewGuid():N}"[..20];
        var res = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "Test bio",
            niche = "cinematic AI composer",
            socialLinks = new[] { new { platform = "twitter", url = "https://twitter.com/test" } },
            showEarnings = true,
            showDownloadStats = false,
        });

        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();

        // Verify envelope shape
        Assert.True(json.GetProperty("success").GetBoolean());
        var data = json.GetProperty("data");

        // Verify all required CreatorProfileDto fields exist
        Assert.True(data.TryGetProperty("id", out _), "Missing 'id'");
        Assert.True(data.TryGetProperty("userId", out _), "Missing 'userId'");
        Assert.True(data.TryGetProperty("slug", out _), "Missing 'slug'");
        Assert.True(data.TryGetProperty("bio", out _), "Missing 'bio'");
        Assert.True(data.TryGetProperty("niche", out _), "Missing 'niche'");
        Assert.True(data.TryGetProperty("profileImageUrl", out _), "Missing 'profileImageUrl'");
        Assert.True(data.TryGetProperty("bannerImageUrl", out _), "Missing 'bannerImageUrl'");
        Assert.True(data.TryGetProperty("socialLinks", out _), "Missing 'socialLinks'");
        Assert.True(data.TryGetProperty("stats", out _), "Missing 'stats'");
        Assert.True(data.TryGetProperty("createdAt", out _), "Missing 'createdAt'");
        Assert.True(data.TryGetProperty("updatedAt", out _), "Missing 'updatedAt'");

        // Verify stats sub-object shape
        var stats = data.GetProperty("stats");
        Assert.True(stats.TryGetProperty("totalDownloads", out _), "Missing 'stats.totalDownloads'");
        Assert.True(stats.TryGetProperty("totalEarnings", out _), "Missing 'stats.totalEarnings'");

        // Verify values match what we sent
        Assert.Equal(slug, data.GetProperty("slug").GetString());
        Assert.Equal("Test bio", data.GetProperty("bio").GetString());
        Assert.Equal("cinematic AI composer", data.GetProperty("niche").GetString());
    }

    [Fact]
    public async Task GetBySlug_ReturnsExpectedShape_AfterCreation()
    {
        var email = $"creator-slug-{Guid.NewGuid():N}@test.com";
        var password = "Test1234!@";
        var client = await CreateCreatorClientAsync(email, password);

        var slug = $"slugtest-{Guid.NewGuid():N}"[..20];
        var upsertRes = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "Slug test bio",
            showEarnings = false,
            showDownloadStats = false,
        });
        upsertRes.EnsureSuccessStatusCode();

        // Now fetch via public endpoint (no auth needed)
        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/creator-profile/{slug}");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        Assert.Equal(slug, data.GetProperty("slug").GetString());
        Assert.Equal("Slug test bio", data.GetProperty("bio").GetString());
        Assert.True(data.TryGetProperty("stats", out _), "Missing 'stats' in public endpoint");
    }

    [Fact]
    public async Task Collections_ReturnsExpectedShape()
    {
        var email = $"creator-coll-{Guid.NewGuid():N}@test.com";
        var password = "Test1234!@";
        var client = await CreateCreatorClientAsync(email, password);

        // Create profile first
        var slug = $"colltest-{Guid.NewGuid():N}"[..20];
        var upsertRes = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "Collections test",
            showEarnings = false,
            showDownloadStats = false,
        });
        upsertRes.EnsureSuccessStatusCode();

        // Create a collection
        var createRes = await client.PostAsJsonAsync("/creator-profile/me/collections", new
        {
            title = "My Album",
            description = "Best beats",
            trackIds = "",
        });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

        var createJson = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var collData = createJson.GetProperty("data");

        // Verify TrackCollectionDto shape
        Assert.True(collData.TryGetProperty("id", out _), "Missing 'id'");
        Assert.True(collData.TryGetProperty("title", out _), "Missing 'title'");
        Assert.True(collData.TryGetProperty("description", out _), "Missing 'description'");
        Assert.True(collData.TryGetProperty("trackIds", out _), "Missing 'trackIds'");
        Assert.True(collData.TryGetProperty("createdAt", out _), "Missing 'createdAt'");
        Assert.True(collData.TryGetProperty("updatedAt", out _), "Missing 'updatedAt'");

        Assert.Equal("My Album", collData.GetProperty("title").GetString());

        // Fetch collections via public endpoint
        var publicClient = _fixture.CreateClient();
        var listRes = await publicClient.GetAsync($"/creator-profile/{slug}/collections");
        listRes.EnsureSuccessStatusCode();

        var listJson = await listRes.Content.ReadFromJsonAsync<JsonElement>();
        var items = listJson.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.True(items.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task SlugConflict_Returns409()
    {
        // User A creates profile with a slug
        var emailA = $"creator-a-{Guid.NewGuid():N}@test.com";
        var passwordA = "Test1234!@";
        var clientA = await CreateCreatorClientAsync(emailA, passwordA);

        var slug = $"unique-{Guid.NewGuid():N}"[..20];
        var resA = await clientA.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "User A",
            showEarnings = false,
            showDownloadStats = false,
        });
        resA.EnsureSuccessStatusCode();

        // User B tries to use the same slug
        var emailB = $"creator-b-{Guid.NewGuid():N}@test.com";
        var passwordB = "Test1234!@";
        var clientB = await CreateCreatorClientAsync(emailB, passwordB);

        var resB = await clientB.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "User B",
            showEarnings = false,
            showDownloadStats = false,
        });
        Assert.Equal(HttpStatusCode.Conflict, resB.StatusCode);
    }

    // ---- F1/F2/F3: Authorization gap tests ----

    [Fact]
    public async Task UpsertProfile_RegularUser_Returns403()
    {
        var email = $"user-{Guid.NewGuid():N}@test.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(email, "Test1234!@");

        var res = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug = "should-fail",
            bio = "I am a regular user",
            showEarnings = false,
            showDownloadStats = false,
        });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task PatchSettings_Unauthenticated_Returns401()
    {
        var client = _fixture.CreateClient();

        var res = await client.PatchAsync("/creator-profile/me/settings",
            JsonContent.Create(new { showEarnings = true }));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task UploadAvatar_Unauthenticated_Returns401()
    {
        var client = _fixture.CreateClient();

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[] { 0xFF, 0xD8 }), "file", "avatar.jpg");
        var res = await client.PostAsync("/creator-profile/me/avatar", form);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task UploadBanner_Unauthenticated_Returns401()
    {
        var client = _fixture.CreateClient();

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[] { 0xFF, 0xD8 }), "file", "banner.jpg");
        var res = await client.PostAsync("/creator-profile/me/banner", form);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    /// <summary>
    /// Register user, set tier to creator in DB, then re-login to get a JWT with tier=creator claim.
    /// </summary>
    private async Task<HttpClient> CreateCreatorClientAsync(string email, string password)
    {
        // Register
        await _fixture.RegisterUserAsync(email, password);

        // Set tier, role, and username in DB to simulate completed creator onboarding
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var users = await db.Users.ToListAsync();
            foreach (var u in users)
            {
                if (u.Email == email)
                {
                    u.Tier = "creator";
                    u.Role = "Creator";
                    u.UserName = $"u{Guid.NewGuid():N}"[..12];
                    u.NormalizedUserName = u.UserName.ToUpperInvariant();
                    break;
                }
            }
            await db.SaveChangesAsync();
        }

        // Re-login to get fresh JWT with tier=creator
        var client = _fixture.CreateClient();
        var loginRes = await client.PostAsJsonAsync("/auth/login", new { email, password });
        loginRes.EnsureSuccessStatusCode();
        var json = await loginRes.Content.ReadFromJsonAsync<JsonElement>();
        var token = json.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
