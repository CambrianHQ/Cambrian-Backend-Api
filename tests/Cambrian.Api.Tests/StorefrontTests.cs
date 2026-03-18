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
/// Integration tests for the storefront endpoint (GET /creator-profile/{slug}/storefront)
/// and pinned-tracks management (PUT /creator-profile/me/pinned-tracks).
/// </summary>
public sealed class StorefrontTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public StorefrontTests(CambrianApiFixture fixture) => _fixture = fixture;

    // ────────────────────────────────────────────────────────────
    //  GET /creator-profile/{slug}/storefront
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Storefront_NonExistentSlug_Returns404()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/creator-profile/no-such-creator/storefront");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Storefront_ReturnsFullShape()
    {
        // Arrange: creator with profile + one public track
        var email = $"sf-shape-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var slug = $"sf-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "Shape test",
            showEarnings = true,
            showDownloadStats = true,
        })).EnsureSuccessStatusCode();

        await _fixture.SeedTrackAsync(userId, "Storefront Beat");

        // Act
        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/creator-profile/{slug}/storefront");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");

        // Assert: top-level envelope
        Assert.True(json.GetProperty("success").GetBoolean());

        // Assert: profile sub-object
        Assert.True(data.TryGetProperty("profile", out var profile));
        Assert.Equal(slug, profile.GetProperty("slug").GetString());

        // Assert: stats sub-object
        Assert.True(data.TryGetProperty("stats", out var stats));
        Assert.True(stats.TryGetProperty("totalDownloads", out _));
        Assert.True(stats.TryGetProperty("totalEarnings", out _));

        // Assert: tracks array
        Assert.True(data.TryGetProperty("tracks", out var tracks));
        Assert.Equal(JsonValueKind.Array, tracks.ValueKind);
        Assert.True(tracks.GetArrayLength() >= 1);

        // Assert: pinnedTracks array
        Assert.True(data.TryGetProperty("pinnedTracks", out var pinned));
        Assert.Equal(JsonValueKind.Array, pinned.ValueKind);

        // Assert: collections array
        Assert.True(data.TryGetProperty("collections", out var collections));
        Assert.Equal(JsonValueKind.Array, collections.ValueKind);
    }

    [Fact]
    public async Task Storefront_OnlyReturnsPublicTracks()
    {
        var email = $"sf-vis-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var slug = $"sv-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug, bio = "Visibility test", showEarnings = false, showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        // Seed one public and one hidden track
        await _fixture.SeedTrackAsync(userId, "Public Beat");
        await SeedTrackWithVisibilityAsync(userId, "Hidden Beat", "hidden");

        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/creator-profile/{slug}/storefront");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var tracks = data.GetProperty("tracks");

        // Only the public track should appear
        Assert.Equal(1, tracks.GetArrayLength());
        Assert.Equal("Public Beat", tracks[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Storefront_ExcludesCopyrightTransferredTracks()
    {
        var email = $"sf-cr-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var slug = $"sc-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug, bio = "Copyright test", showEarnings = false, showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        await _fixture.SeedTrackAsync(userId, "Normal Beat");
        await SeedTrackWithStatusAsync(userId, "Transferred Beat", "copyright_transferred");

        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/creator-profile/{slug}/storefront");
        res.EnsureSuccessStatusCode();

        var tracks = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("tracks");

        Assert.Equal(1, tracks.GetArrayLength());
        Assert.Equal("Normal Beat", tracks[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Storefront_ExcludesExclusiveSoldTracks()
    {
        var email = $"sf-ex-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var slug = $"se-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug, bio = "Exclusive test", showEarnings = false, showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        await _fixture.SeedTrackAsync(userId, "Available Beat");
        await SeedExclusiveSoldTrackAsync(userId, "Sold Beat");

        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/creator-profile/{slug}/storefront");
        res.EnsureSuccessStatusCode();

        var tracks = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("tracks");

        Assert.Equal(1, tracks.GetArrayLength());
        Assert.Equal("Available Beat", tracks[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Storefront_PinnedTracks_FallsBackToMostRecent5()
    {
        var email = $"sf-pin-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var slug = $"sp-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug, bio = "Pinned fallback test", showEarnings = false, showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        // Seed 7 tracks — with no pinned IDs set, storefront should pin the 5 most recent
        for (var i = 1; i <= 7; i++)
            await _fixture.SeedTrackAsync(userId, $"Beat {i}");

        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/creator-profile/{slug}/storefront");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var pinned = data.GetProperty("pinnedTracks");

        Assert.Equal(5, pinned.GetArrayLength());
    }

    [Fact]
    public async Task Storefront_PinnedTracks_RespectsCreatorOrder()
    {
        var email = $"sf-pord-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var slug = $"so-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug, bio = "Pinned order test", showEarnings = false, showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        var trackA = await _fixture.SeedTrackAsync(userId, "Beat A");
        var trackB = await _fixture.SeedTrackAsync(userId, "Beat B");
        var trackC = await _fixture.SeedTrackAsync(userId, "Beat C");

        // Set pinned order: C, A (skip B)
        var pinnedIds = $"{trackC},{trackA}";
        var pinRes = await client.PutAsJsonAsync("/creator-profile/me/pinned-tracks", new { trackIds = pinnedIds });
        pinRes.EnsureSuccessStatusCode();

        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/creator-profile/{slug}/storefront");
        res.EnsureSuccessStatusCode();

        var pinned = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("pinnedTracks");

        Assert.Equal(2, pinned.GetArrayLength());
        Assert.Equal(trackC.ToString(), pinned[0].GetProperty("id").GetString());
        Assert.Equal(trackA.ToString(), pinned[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Storefront_Stats_HidesEarnings_WhenToggled()
    {
        var email = $"sf-earn-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var slug = $"sh-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug, bio = "Earnings toggle test", showEarnings = false, showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        // Seed a completed purchase
        var trackId = await _fixture.SeedTrackAsync(userId, "Purchased Beat");
        await SeedCompletedPurchaseAsync(userId, trackId, 2999);

        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/creator-profile/{slug}/storefront");
        res.EnsureSuccessStatusCode();

        var stats = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("stats");

        // earnings should be 0 because ShowEarnings is false
        Assert.Equal(0m, stats.GetProperty("totalEarnings").GetDecimal());
        // downloads should still count
        Assert.Equal(1, stats.GetProperty("totalDownloads").GetInt32());
    }

    [Fact]
    public async Task Storefront_Stats_ShowsEarnings_WhenEnabled()
    {
        var email = $"sf-earny-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var slug = $"sy-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug, bio = "Earnings visible test", showEarnings = true, showDownloadStats = true,
        })).EnsureSuccessStatusCode();

        var trackId = await _fixture.SeedTrackAsync(userId, "Earning Beat");
        await SeedCompletedPurchaseAsync(userId, trackId, 5000); // $50.00

        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/creator-profile/{slug}/storefront");
        res.EnsureSuccessStatusCode();

        var stats = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("stats");

        Assert.Equal(50m, stats.GetProperty("totalEarnings").GetDecimal());
        Assert.Equal(1, stats.GetProperty("totalDownloads").GetInt32());
    }

    [Fact]
    public async Task Storefront_IncludesCollections()
    {
        var email = $"sf-coll-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);

        var slug = $"sk-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug, bio = "Collections test", showEarnings = false, showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        (await client.PostAsJsonAsync("/creator-profile/me/collections", new
        {
            title = "Album One",
            description = "First album",
            trackIds = "",
        })).EnsureSuccessStatusCode();

        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/creator-profile/{slug}/storefront");
        res.EnsureSuccessStatusCode();

        var collections = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("collections");

        Assert.Equal(1, collections.GetArrayLength());
        Assert.Equal("Album One", collections[0].GetProperty("title").GetString());
    }

    // ────────────────────────────────────────────────────────────
    //  PUT /creator-profile/me/pinned-tracks
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PinnedTracks_Unauthenticated_Returns401()
    {
        var client = _fixture.CreateClient();
        var res = await client.PutAsJsonAsync("/creator-profile/me/pinned-tracks", new { trackIds = "" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task PinnedTracks_NoProfile_Returns404()
    {
        // Register a creator-tier user but do NOT create a profile
        var email = $"sf-nopr-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);

        var res = await client.PutAsJsonAsync("/creator-profile/me/pinned-tracks", new { trackIds = "" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task PinnedTracks_Update_ReturnsSavedIds()
    {
        var email = $"sf-pinu-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        var slug = $"su-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug, bio = "Pin update test", showEarnings = false, showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        var trackA = await _fixture.SeedTrackAsync(userId, "Pin A");
        var trackB = await _fixture.SeedTrackAsync(userId, "Pin B");
        var ids = $"{trackA},{trackB}";

        var res = await client.PutAsJsonAsync("/creator-profile/me/pinned-tracks", new { trackIds = ids });
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var saved = json.GetProperty("data").GetProperty("pinnedTrackIds").GetString();
        Assert.Equal(ids, saved);
    }

    [Fact]
    public async Task PinnedTracks_ClearPins()
    {
        var email = $"sf-clr-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);

        var slug = $"sr-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug, bio = "Clear pins", showEarnings = false, showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        // Set then clear
        var res = await client.PutAsJsonAsync("/creator-profile/me/pinned-tracks", new { trackIds = "" });
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var saved = json.GetProperty("data").GetProperty("pinnedTrackIds").GetString();
        Assert.Equal("", saved);
    }

    // ────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Register user, set tier to "creator" in DB, re-login to get a JWT with tier=creator.
    /// Mirrors the pattern from CreatorProfileContractTests.
    /// </summary>
    private async Task<HttpClient> CreateCreatorClientAsync(string email)
    {
        var password = "Test1234!@";
        await _fixture.RegisterUserAsync(email, password);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email);
            user.Tier = "creator";
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

    private async Task SeedTrackWithVisibilityAsync(string creatorId, string title, string visibility)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var id = Guid.NewGuid();
        db.Tracks.Add(new Track
        {
            Id = id,
            CambrianTrackId = $"CAMB-TRK-{id.ToString()[..8].ToUpper()}",
            Title = title,
            Price = 9.99,
            LicenseType = "standard",
            AudioUrl = "tracks/test.mp3",
            CreatorId = creatorId,
            Genre = "Electronic",
            Visibility = visibility,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedTrackWithStatusAsync(string creatorId, string title, string status)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var id = Guid.NewGuid();
        db.Tracks.Add(new Track
        {
            Id = id,
            CambrianTrackId = $"CAMB-TRK-{id.ToString()[..8].ToUpper()}",
            Title = title,
            Price = 9.99,
            LicenseType = "standard",
            AudioUrl = "tracks/test.mp3",
            CreatorId = creatorId,
            Genre = "Electronic",
            Status = status,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedExclusiveSoldTrackAsync(string creatorId, string title)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var id = Guid.NewGuid();
        db.Tracks.Add(new Track
        {
            Id = id,
            CambrianTrackId = $"CAMB-TRK-{id.ToString()[..8].ToUpper()}",
            Title = title,
            Price = 9.99,
            LicenseType = "standard",
            AudioUrl = "tracks/test.mp3",
            CreatorId = creatorId,
            Genre = "Electronic",
            ExclusiveSold = true,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedCompletedPurchaseAsync(string creatorId, Guid trackId, int amountCents)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        // Need a buyer — register or find one
        var buyerId = "buyer-" + Guid.NewGuid().ToString("N")[..8];
        db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = buyerId,
            TrackId = trackId,
            AmountCents = amountCents,
            Status = "completed",
            LicenseType = "non-exclusive",
            PaymentMethod = "stripe",
            CompletedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
