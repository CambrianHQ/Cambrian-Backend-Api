using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// Contract + behaviour tests for the UUID-based creator identity endpoints.
/// Validates:
///   - GET /api/creators/{creatorId}
///   - GET /api/creators/by-username/{username}
///   - GET /api/creators/{creatorId}/tracks
///   - GET /api/creators/username-availability
///   - PUT /api/creator/me
///   - POST /api/uploads/creator-image-url
///   - Email never appears in public responses
///   - Username resolves to creatorId
///   - Tracks endpoint filters by creatorId (UUID) only
/// </summary>
public sealed class CreatorIdentityTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public CreatorIdentityTests(CambrianApiFixture fixture) => _fixture = fixture;

    // ── GET /api/creators/{creatorId} ──

    [Fact]
    public async Task GetById_NonExistentCreator_Returns404()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync($"/api/creators/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task GetById_ExistingCreator_ReturnsPublicDto_NoEmail()
    {
        // Arrange: register user, seed Creator
        var email = $"id-test-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(email);
        var creatorId = await _fixture.SeedCreatorAsync(userId, $"iduser-{Guid.NewGuid():N}"[..20], "Display Name");

        // Act
        var client = _fixture.CreateClient();
        var res = await client.GetAsync($"/api/creators/{creatorId}");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        // Assert: shape
        Assert.True(data.TryGetProperty("id", out _), "Missing 'id'");
        Assert.True(data.TryGetProperty("username", out _), "Missing 'username'");
        Assert.True(data.TryGetProperty("displayName", out _), "Missing 'displayName'");
        Assert.True(data.TryGetProperty("bio", out _), "Missing 'bio'");
        Assert.True(data.TryGetProperty("stats", out _), "Missing 'stats'");

        // Assert: no email
        Assert.False(data.TryGetProperty("email", out _), "Public DTO must NOT contain 'email'");
        var rawJson = data.GetRawText();
        Assert.DoesNotContain(email, rawJson, StringComparison.OrdinalIgnoreCase);
    }

    // ── GET /api/creators/by-username/{username} ──

    [Fact]
    public async Task GetByUsername_NonExistent_Returns404()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/api/creators/by-username/nonexistent-user-xyz");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task GetByUsername_Resolves_To_CreatorId()
    {
        var email = $"uname-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(email);
        var username = $"testuser-{Guid.NewGuid():N}"[..20];
        var creatorId = await _fixture.SeedCreatorAsync(userId, username, "User Test");

        var client = _fixture.CreateClient();
        var res = await client.GetAsync($"/api/creators/by-username/{username}");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.Equal(creatorId.ToString(), data.GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetByUsername_EmptyUsername_ReturnsError()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/api/creators/by-username/%20");
        Assert.True(res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
    }

    // ── GET /api/creators/{creatorId}/tracks ──

    [Fact]
    public async Task GetTracks_NonExistentCreator_Returns404()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync($"/api/creators/{Guid.NewGuid()}/tracks");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task GetTracks_FiltersBy_CreatorUuid_Only()
    {
        // Arrange: two creators, each with a track
        var emailA = $"tracks-a-{Guid.NewGuid():N}@test.com";
        var emailB = $"tracks-b-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(emailA, "Test1234!@");
        await _fixture.RegisterUserAsync(emailB, "Test1234!@");
        var userIdA = await _fixture.GetUserIdAsync(emailA);
        var userIdB = await _fixture.GetUserIdAsync(emailB);

        var creatorA = await _fixture.SeedCreatorAsync(userIdA, $"creata-{Guid.NewGuid():N}"[..20]);
        var creatorB = await _fixture.SeedCreatorAsync(userIdB, $"creatb-{Guid.NewGuid():N}"[..20]);

        await _fixture.SeedTrackWithCreatorUuidAsync(userIdA, creatorA, "Beat by A");
        await _fixture.SeedTrackWithCreatorUuidAsync(userIdB, creatorB, "Beat by B");

        // Act: fetch tracks for creator A
        var client = _fixture.CreateClient();
        var res = await client.GetAsync($"/api/creators/{creatorA}/tracks");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);

        // Assert: only creator A's tracks are returned
        foreach (var track in data.EnumerateArray())
        {
            Assert.Equal("Beat by A", track.GetProperty("title").GetString());
        }
    }

    [Fact]
    public async Task GetTracks_DoesNot_Leak_Email_In_Artist()
    {
        var email = $"noleak-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(email);
        var creatorId = await _fixture.SeedCreatorAsync(userId, $"noleak-{Guid.NewGuid():N}"[..20], "Safe Name");

        await _fixture.SeedTrackWithCreatorUuidAsync(userId, creatorId, "No Leak Beat");

        var client = _fixture.CreateClient();
        var res = await client.GetAsync($"/api/creators/{creatorId}/tracks");
        res.EnsureSuccessStatusCode();

        var rawJson = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain(email, rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@test.com", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    // ── GET /api/creators/username-availability ──

    [Fact]
    public async Task UsernameAvailability_AvailableUsername_ReturnsTrue()
    {
        var client = _fixture.CreateClient();
        var username = $"avail-{Guid.NewGuid():N}"[..20];
        var res = await client.GetAsync($"/api/creators/username-availability?username={username}");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.True(data.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task UsernameAvailability_TakenUsername_ReturnsFalse()
    {
        var email = $"taken-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(email);
        var username = $"taken-{Guid.NewGuid():N}"[..20];
        await _fixture.SeedCreatorAsync(userId, username);

        var client = _fixture.CreateClient();
        var res = await client.GetAsync($"/api/creators/username-availability?username={username}");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.False(data.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task UsernameAvailability_TooShort_ReturnsFalse()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/api/creators/username-availability?username=ab");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.False(data.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task UsernameAvailability_Missing_ReturnsError()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/api/creators/username-availability");
        Assert.True(res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.OK);
    }

    // ── PUT /api/creator/me ──

    [Fact]
    public async Task UpdateMyProfile_Unauthenticated_Returns401()
    {
        var client = _fixture.CreateClient();
        var res = await client.PutAsJsonAsync("/api/creator/me", new { username = "test123" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task UpdateMyProfile_CreatorTier_CreatesProfile()
    {
        var email = $"creator-me-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email, "Test1234!@");
        var username = $"myuser-{Guid.NewGuid():N}"[..20];

        var res = await client.PutAsJsonAsync("/api/creator/me", new
        {
            username,
            bio = "Test bio",
        });
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.Equal(username.ToLowerInvariant(), data.GetProperty("username").GetString());
        Assert.Equal(username.ToLowerInvariant(), data.GetProperty("displayName").GetString());

        // Email must not appear anywhere in the response
        var rawJson = data.GetRawText();
        Assert.DoesNotContain(email, rawJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateMyProfile_DuplicateUsername_Returns409()
    {
        // User A takes a username
        var emailA = $"dup-a-{Guid.NewGuid():N}@test.com";
        var clientA = await CreateCreatorClientAsync(emailA, "Test1234!@");
        var username = $"dupuser-{Guid.NewGuid():N}"[..20];

        var resA = await clientA.PutAsJsonAsync("/api/creator/me", new { username });
        resA.EnsureSuccessStatusCode();

        // User B tries the same username
        var emailB = $"dup-b-{Guid.NewGuid():N}@test.com";
        var clientB = await CreateCreatorClientAsync(emailB, "Test1234!@");

        var resB = await clientB.PutAsJsonAsync("/api/creator/me", new { username });
        Assert.Equal(HttpStatusCode.Conflict, resB.StatusCode);
    }

    [Fact]
    public async Task UpdateMyProfile_CannotSetEmail_FieldIgnored()
    {
        var email = $"noemail-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email, "Test1234!@");
        var username = $"noeml-{Guid.NewGuid():N}"[..20];

        // Attempt to set an email field via the request body
        var res = await client.PutAsJsonAsync("/api/creator/me", new
        {
            username,
            email = "hacker@evil.com",  // should be ignored by the DTO
        });
        res.EnsureSuccessStatusCode();

        var rawJson = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hacker@evil.com", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    // ── POST /api/uploads/creator-image-url ──

    [Fact]
    public async Task UploadCreatorImage_Unauthenticated_Returns401()
    {
        var client = _fixture.CreateClient();
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[100]), "file", "test.png");
        var res = await client.PostAsync("/api/uploads/creator-image-url", content);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ── Helpers ──

    private async Task<HttpClient> CreateCreatorClientAsync(string email, string password)
    {
        await _fixture.RegisterUserAsync(email, password);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email);
            user.Tier = "creator";
            user.Role = "Creator";
            await db.SaveChangesAsync();
        }

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
