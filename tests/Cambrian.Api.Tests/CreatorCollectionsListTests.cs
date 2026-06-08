using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// B5 regression: listing a creator's collections by username (or via me/collections) must return
/// created collections. The list handler previously looked up the profile by slug using the
/// username, which differs from the profile slug, and 404'd a creator that demonstrably exists.
/// </summary>
public sealed class CreatorCollectionsListTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public CreatorCollectionsListTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Collections_ListByUsername_And_Me_WhenSlugDiffers_ReturnItems()
    {
        var (client, username) = await SetupCreatorWithDistinctSlugAsync();

        var createRes = await client.PostAsJsonAsync("/creator-profile/me/collections", new
        {
            title = "Keep Album",
            description = "x",
            trackIds = "",
        });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

        // Public list by username (route passes a username; profile slug differs).
        var publicClient = _fixture.CreateClient();
        var byUsername = await publicClient.GetAsync($"/creator/username/{username}/collections");
        byUsername.EnsureSuccessStatusCode();
        var items = (await byUsername.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.True(items.GetArrayLength() >= 1);

        // Authenticated me/collections also lists them.
        var mine = await client.GetAsync("/creator-profile/me/collections");
        mine.EnsureSuccessStatusCode();
        var mineItems = (await mine.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(JsonValueKind.Array, mineItems.ValueKind);
        Assert.True(mineItems.GetArrayLength() >= 1);
    }

    /// <summary>
    /// Creates a creator-tier user with a Creator identity row whose username intentionally
    /// differs from the CreatorProfile slug, and returns an authenticated client + the username.
    /// </summary>
    private async Task<(HttpClient client, string username)> SetupCreatorWithDistinctSlugAsync()
    {
        var email = $"coll-{Guid.NewGuid():N}@test.com";
        var password = "Test1234!@";
        await _fixture.RegisterUserAsync(email, password);
        var userId = await _fixture.GetUserIdAsync(email);
        var username = $"user{Guid.NewGuid():N}"[..14];

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email);
            user.Tier = "creator";
            user.Role = "Creator";
            user.UserName = username;
            user.NormalizedUserName = username.ToUpperInvariant();
            await db.SaveChangesAsync();
        }

        await _fixture.SeedCreatorAsync(userId, username);

        var token = await _fixture.LoginUserAsync(email, password);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Slug intentionally different from the username.
        var slug = $"slug-{Guid.NewGuid():N}"[..18];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "collections",
            showEarnings = false,
            showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        return (client, username);
    }
}
