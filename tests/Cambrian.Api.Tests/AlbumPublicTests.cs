using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// Public album (track collection) surface tests:
/// GET /collections/{id} (anonymous album detail), the public per-creator
/// collections listing, and owner-only visibility of hidden albums.
/// Also guards the PublicCatalogTrackDto allowlist inside the album detail
/// payload (F18-style leak check: no exclusive/buyout pricing, no visibility).
/// </summary>
public sealed class AlbumPublicTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public AlbumPublicTests(CambrianApiFixture fixture) => _fixture = fixture;

    // ───── 1. Public album detail: hydrated tracks in position order ─────

    [Fact]
    public async Task CollectionDetail_PublicAlbum_Anonymous_ReturnsHydratedTracksInOrder()
    {
        var creator = await SetupCreatorAsync();
        var trackA = await _fixture.SeedTrackAsync(creator.UserId, "Album Track A");
        var trackB = await _fixture.SeedTrackAsync(creator.UserId, "Album Track B");

        // B first, A second — the CSV order defines the album track positions.
        var collection = await CreateCollectionAsync(creator.Client, new
        {
            title = "Public Order Album",
            description = "order test",
            trackIds = $"{trackB},{trackA}",
            visibility = "public",
        });
        var collectionId = collection.GetProperty("id").GetString()!;

        var anon = _fixture.CreateClient();
        var res = await anon.GetAsync($"/collections/{collectionId}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("title").GetString().Should().Be("Public Order Album");
        data.GetProperty("visibility").GetString().Should().Be("public");

        // Hydrated tracks come back in the stored position order (B then A).
        var tracks = data.GetProperty("tracks");
        tracks.ValueKind.Should().Be(JsonValueKind.Array);
        tracks.GetArrayLength().Should().Be(2);
        Guid.Parse(tracks[0].GetProperty("id").GetString()!).Should().Be(trackB);
        Guid.Parse(tracks[1].GetProperty("id").GetString()!).Should().Be(trackA);
        tracks[0].GetProperty("title").GetString().Should().Be("Album Track B");

        // trackIds mirrors the same order.
        var trackIds = data.GetProperty("trackIds");
        trackIds.GetArrayLength().Should().Be(2);
        Guid.Parse(trackIds[0].GetString()!).Should().Be(trackB);
        Guid.Parse(trackIds[1].GetString()!).Should().Be(trackA);

        // Creator summary carries the public identity used for storefront links.
        var summary = data.GetProperty("creator");
        summary.GetProperty("userId").GetString().Should().Be(creator.UserId);
        summary.GetProperty("username").GetString().Should().Be(creator.Username);
        summary.GetProperty("slug").GetString().Should().Be(creator.Slug);
    }

    // ───── 2. Hidden album: 404 for anon, 200 for owner ─────

    [Fact]
    public async Task CollectionDetail_HiddenAlbum_Anon404_Owner200()
    {
        var creator = await SetupCreatorAsync();
        var collection = await CreateCollectionAsync(creator.Client, new
        {
            title = "Hidden Album",
            trackIds = "",
            visibility = "private",
        });
        var collectionId = collection.GetProperty("id").GetString()!;

        var anon = _fixture.CreateClient();
        var anonRes = await anon.GetAsync($"/collections/{collectionId}");
        anonRes.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var ownerRes = await creator.Client.GetAsync($"/collections/{collectionId}");
        ownerRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await ownerRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("visibility").GetString().Should().Be("private");
    }

    // ───── 3. Draft (hidden) track inside a public album is filtered for anon ─────

    [Fact]
    public async Task CollectionDetail_HiddenTrackInAlbum_FilteredFromHydratedTracks()
    {
        var creator = await SetupCreatorAsync();
        var publicTrack = await _fixture.SeedTrackAsync(creator.UserId, "Visible Track");
        var draftTrack = await _fixture.SeedTrackAsync(creator.UserId, "Draft Track");

        var collection = await CreateCollectionAsync(creator.Client, new
        {
            title = "Mixed Visibility Album",
            trackIds = $"{publicTrack},{draftTrack}",
            visibility = "public",
        });
        var collectionId = collection.GetProperty("id").GetString()!;

        // Hide the second track AFTER it joined the album (draft state).
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var track = await db.Tracks.FirstAsync(t => t.Id == draftTrack);
            track.Visibility = "hidden";
            await db.SaveChangesAsync();
        }

        var anon = _fixture.CreateClient();
        var res = await anon.GetAsync($"/collections/{collectionId}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        // The album stays 200 but the draft track is filtered out of tracks[].
        var tracks = data.GetProperty("tracks");
        tracks.GetArrayLength().Should().Be(1);
        Guid.Parse(tracks[0].GetProperty("id").GetString()!).Should().Be(publicTrack);

        // trackIds is the raw membership list and may still include the hidden track.
        var rawIds = data.GetProperty("trackIds").EnumerateArray()
            .Select(e => Guid.Parse(e.GetString()!))
            .ToList();
        rawIds.Should().Contain(publicTrack);
    }

    // ───── 4. Public per-creator list: hidden albums are owner-only ─────

    [Fact]
    public async Task CollectionsList_BySlug_HiddenAlbum_AnonAbsent_OwnerPresent()
    {
        var creator = await SetupCreatorAsync();
        var publicAlbum = await CreateCollectionAsync(creator.Client, new
        {
            title = "List Public Album",
            trackIds = "",
            visibility = "public",
        });
        var hiddenAlbum = await CreateCollectionAsync(creator.Client, new
        {
            title = "List Hidden Album",
            trackIds = "",
            visibility = "private",
        });
        var publicId = publicAlbum.GetProperty("id").GetString()!;
        var hiddenId = hiddenAlbum.GetProperty("id").GetString()!;

        // Anonymous listing must not include the hidden album.
        var anon = _fixture.CreateClient();
        var anonRes = await anon.GetAsync($"/creator-profile/{creator.Slug}/collections");
        anonRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var anonIds = await ReadCollectionIdsAsync(anonRes);
        anonIds.Should().Contain(publicId);
        anonIds.Should().NotContain(hiddenId);

        // The owner hitting the SAME public route sees the hidden album.
        var ownerRes = await creator.Client.GetAsync($"/creator-profile/{creator.Slug}/collections");
        ownerRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var ownerIds = await ReadCollectionIdsAsync(ownerRes);
        ownerIds.Should().Contain(publicId);
        ownerIds.Should().Contain(hiddenId);
    }

    // ───── 5. me/collections includes hidden albums ─────

    [Fact]
    public async Task MyCollections_IncludesHiddenAlbums()
    {
        var creator = await SetupCreatorAsync();
        var hiddenAlbum = await CreateCollectionAsync(creator.Client, new
        {
            title = "My Hidden Album",
            trackIds = "",
            visibility = "private",
        });
        var hiddenId = hiddenAlbum.GetProperty("id").GetString()!;

        var res = await creator.Client.GetAsync("/creator-profile/me/collections");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var ids = await ReadCollectionIdsAsync(res);
        ids.Should().Contain(hiddenId);
    }

    // ───── 6. Unknown collection id ─────

    [Fact]
    public async Task CollectionDetail_UnknownId_Returns404()
    {
        var anon = _fixture.CreateClient();
        var res = await anon.GetAsync($"/collections/{Guid.NewGuid()}");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ───── 7. PublicCatalogTrackDto allowlist: no private fields in tracks[] ─────

    [Fact]
    public async Task CollectionDetail_TrackItems_DoNotLeakPrivateFields()
    {
        var creator = await SetupCreatorAsync();
        // Legacy pricing set on the ENTITY — the test proves it never reaches the payload.
        var trackId = await SeedTrackWithLegacyPricingAsync(creator.UserId, "Leak Probe Album Track");

        var collection = await CreateCollectionAsync(creator.Client, new
        {
            title = "Leak Probe Album",
            trackIds = trackId.ToString(),
            visibility = "public",
        });
        var collectionId = collection.GetProperty("id").GetString()!;

        var anon = _fixture.CreateClient();
        var res = await anon.GetAsync($"/collections/{collectionId}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var tracks = data.GetProperty("tracks");
        tracks.GetArrayLength().Should().Be(1);

        var forbiddenKeys = new[] { "exclusivePriceCents", "copyrightBuyoutPriceCents", "visibility" };
        foreach (var item in tracks.EnumerateArray())
        {
            foreach (var prop in item.EnumerateObject())
            {
                forbiddenKeys.Should().NotContain(
                    k => string.Equals(k, prop.Name, StringComparison.OrdinalIgnoreCase),
                    $"tracks[] items must not expose private field '{prop.Name}'");
            }
        }
    }

    // ───── Helpers ─────

    private sealed record CreatorContext(HttpClient Client, string UserId, string Username, string Slug);

    /// <summary>
    /// Register a user, promote to creator tier/role with a username + Creator identity
    /// row, create a profile with a known slug, and return an authenticated client.
    /// Mirrors CreatorProfileContractTests / CreatorCollectionsListTests setup.
    /// </summary>
    private async Task<CreatorContext> SetupCreatorAsync()
    {
        var email = $"album-{Guid.NewGuid():N}@test.com";
        var password = "Test1234!@";
        await _fixture.RegisterUserAsync(email, password);
        var userId = await _fixture.GetUserIdAsync(email);
        var username = $"alb{Guid.NewGuid():N}"[..12];

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

        // Re-login so the JWT carries the creator tier/role claims.
        var token = await _fixture.LoginUserAsync(email, password);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var slug = $"alb-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "album public tests",
            showEarnings = false,
            showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        return new CreatorContext(client, userId, username, slug);
    }

    /// <summary>POST a collection and return the unwrapped TrackCollectionDto data element.</summary>
    private static async Task<JsonElement> CreateCollectionAsync(HttpClient client, object body)
    {
        var res = await client.PostAsJsonAsync("/creator-profile/me/collections", body);
        var raw = await res.Content.ReadAsStringAsync();
        // Plain xUnit assert: the raw body may contain '{', which FluentAssertions'
        // because-formatting would choke on.
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"collection create failed ({(int)res.StatusCode}): {raw}");
        return JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("data");
    }

    /// <summary>Read the envelope's data array and return the collection ids.</summary>
    private static async Task<List<string>> ReadCollectionIdsAsync(HttpResponseMessage res)
    {
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.ValueKind.Should().Be(JsonValueKind.Array);
        return data.EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToList();
    }

    /// <summary>
    /// Seed a public track whose entity carries retired exclusive/buyout pricing,
    /// so the leak test exercises real data (mirrors LicensingLeakRegressionTests).
    /// </summary>
    private async Task<Guid> SeedTrackWithLegacyPricingAsync(string creatorUserId, string title)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var id = Guid.NewGuid();
        db.Tracks.Add(new Track
        {
            Id = id,
            CambrianTrackId = $"CAMB-TRK-{id.ToString()[..8].ToUpper()}",
            Title = title,
            Price = 9.99m,
            NonExclusivePriceCents = 999,
            ExclusivePriceCents = 49900,
            CopyrightBuyoutPriceCents = 249900,
            LicenseType = "standard",
            AudioUrl = "tracks/album-leak-test.mp3",
            CreatorId = creatorUserId,
            Genre = "Electronic",
            Visibility = "public",
        });
        await db.SaveChangesAsync();
        return id;
    }
}
