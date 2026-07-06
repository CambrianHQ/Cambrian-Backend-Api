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

namespace Cambrian.Api.Tests.V1;

/// <summary>
/// Public album discovery over the versioned API
/// (<c>/api/v1/public/albums/{slug}</c> and
/// <c>/api/v1/public/creators/{username}/albums</c>): visibility enforcement,
/// hydrated-track ordering, and the public-DTO leak guard (no exclusive/buyout
/// pricing, no earnings, no album visibility surfacing on the wrong routes).
/// </summary>
public sealed class PublicAlbumsV1Tests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public PublicAlbumsV1Tests(CambrianApiFixture fixture) => _fixture = fixture;

    // ───── Public detail by slug ─────

    [Fact]
    public async Task PublicAlbum_BySlug_Anonymous_ReturnsHydratedTracksInOrder()
    {
        var creator = await SetupCreatorAsync();
        var trackA = await _fixture.SeedTrackAsync(creator.UserId, "Album Track A");
        var trackB = await _fixture.SeedTrackAsync(creator.UserId, "Album Track B");

        // B then A defines the album order.
        var album = await CreateAlbumAsync(creator.Client, new
        {
            title = "Public V1 Order",
            visibility = "public",
            trackIds = new[] { trackB.ToString(), trackA.ToString() },
        });
        var slug = album.GetProperty("slug").GetString()!;

        var res = await _fixture.CreateClient().GetAsync($"/api/v1/public/albums/{slug}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        data.GetProperty("visibility").GetString().Should().Be("public");
        var tracks = data.GetProperty("tracks");
        tracks.GetArrayLength().Should().Be(2);
        Guid.Parse(tracks[0].GetProperty("id").GetString()!).Should().Be(trackB);
        Guid.Parse(tracks[1].GetProperty("id").GetString()!).Should().Be(trackA);

        var summary = data.GetProperty("creator");
        summary.GetProperty("userId").GetString().Should().Be(creator.UserId);
        summary.GetProperty("username").GetString().Should().Be(creator.Username);
        summary.GetProperty("slug").GetString().Should().Be(creator.Slug);
    }

    [Fact]
    public async Task PublicAlbum_ByAlbumId_AlsoResolves()
    {
        var creator = await SetupCreatorAsync();
        var album = await CreateAlbumAsync(creator.Client, new { title = "By Id", visibility = "public" });
        var id = album.GetProperty("id").GetString()!;

        var res = await _fixture.CreateClient().GetAsync($"/api/v1/public/albums/{id}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PublicAlbum_Unlisted_ReachableByLink_ButNotListed()
    {
        var creator = await SetupCreatorAsync();
        var album = await CreateAlbumAsync(creator.Client, new { title = "Secret Link", visibility = "unlisted" });
        var slug = album.GetProperty("slug").GetString()!;

        // Reachable by direct slug link.
        (await _fixture.CreateClient().GetAsync($"/api/v1/public/albums/{slug}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // But absent from the creator's public listing.
        var listRes = await _fixture.CreateClient().GetAsync($"/api/v1/public/creators/{creator.Username}/albums");
        var ids = await ReadAlbumIdsAsync(listRes);
        ids.Should().NotContain(album.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("private")]
    public async Task PublicAlbum_DraftOrPrivate_Returns404ForEveryone(string visibility)
    {
        var creator = await SetupCreatorAsync();
        var album = await CreateAlbumAsync(creator.Client, new { title = $"Hidden {visibility}", visibility });
        var slug = album.GetProperty("slug").GetString()!;
        var id = album.GetProperty("id").GetString()!;

        (await _fixture.CreateClient().GetAsync($"/api/v1/public/albums/{slug}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        // Even the owner cannot reach a draft/private album through the PUBLIC route.
        (await creator.Client.GetAsync($"/api/v1/public/albums/{id}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PublicAlbum_UnknownSlug_Returns404()
    {
        (await _fixture.CreateClient().GetAsync($"/api/v1/public/albums/no-such-album-{Guid.NewGuid():N}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ───── Draft track inside a public album is filtered ─────

    [Fact]
    public async Task PublicAlbum_HiddenTrackInPublicAlbum_FilteredFromHydratedTracks()
    {
        var creator = await SetupCreatorAsync();
        var publicTrack = await _fixture.SeedTrackAsync(creator.UserId, "Visible");
        var draftTrack = await _fixture.SeedTrackAsync(creator.UserId, "Draft");
        var album = await CreateAlbumAsync(creator.Client, new
        {
            title = "Mixed Visibility",
            visibility = "public",
            trackIds = new[] { publicTrack.ToString(), draftTrack.ToString() },
        });
        var slug = album.GetProperty("slug").GetString()!;

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var t = await db.Tracks.FirstAsync(x => x.Id == draftTrack);
            t.Visibility = "hidden";
            await db.SaveChangesAsync();
        }

        var res = await _fixture.CreateClient().GetAsync($"/api/v1/public/albums/{slug}");
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var tracks = data.GetProperty("tracks");
        tracks.GetArrayLength().Should().Be(1);
        Guid.Parse(tracks[0].GetProperty("id").GetString()!).Should().Be(publicTrack);
    }

    // ───── Leak guard ─────

    [Fact]
    public async Task PublicAlbum_TrackItems_DoNotLeakPrivateFields()
    {
        var creator = await SetupCreatorAsync();
        var trackId = await SeedTrackWithLegacyPricingAsync(creator.UserId, "Leak Probe");
        var album = await CreateAlbumAsync(creator.Client, new
        {
            title = "Leak Probe Album",
            visibility = "public",
            trackIds = new[] { trackId.ToString() },
        });
        var slug = album.GetProperty("slug").GetString()!;

        var res = await _fixture.CreateClient().GetAsync($"/api/v1/public/albums/{slug}");
        var raw = await res.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("data");

        // Envelope-wide: no earnings/fee/payout wording anywhere in the payload.
        foreach (var banned in new[] { "earning", "platformFee", "payout", "revenue", "exclusivePriceCents", "copyrightBuyoutPriceCents" })
            raw.ToLowerInvariant().Should().NotContain(banned.ToLowerInvariant());

        var forbiddenTrackKeys = new[] { "exclusivePriceCents", "copyrightBuyoutPriceCents", "visibility" };
        foreach (var item in data.GetProperty("tracks").EnumerateArray())
            foreach (var prop in item.EnumerateObject())
                forbiddenTrackKeys.Should().NotContain(
                    k => string.Equals(k, prop.Name, StringComparison.OrdinalIgnoreCase),
                    $"track item leaked '{prop.Name}'");
    }

    // ───── Creator public listing ─────

    [Fact]
    public async Task CreatorAlbums_ReturnsOnlyPublicAlbums()
    {
        var creator = await SetupCreatorAsync();
        var pub = await CreateAlbumAsync(creator.Client, new { title = "Listed", visibility = "public" });
        var draft = await CreateAlbumAsync(creator.Client, new { title = "Draft", visibility = "draft" });
        var unlisted = await CreateAlbumAsync(creator.Client, new { title = "Unlisted", visibility = "unlisted" });
        var priv = await CreateAlbumAsync(creator.Client, new { title = "Private", visibility = "private" });

        var res = await _fixture.CreateClient().GetAsync($"/api/v1/public/creators/{creator.Username}/albums");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var ids = await ReadAlbumIdsAsync(res);

        ids.Should().Contain(pub.GetProperty("id").GetString());
        ids.Should().NotContain(draft.GetProperty("id").GetString());
        ids.Should().NotContain(unlisted.GetProperty("id").GetString());
        ids.Should().NotContain(priv.GetProperty("id").GetString());
    }

    [Fact]
    public async Task CreatorAlbums_UnknownCreator_Returns404()
    {
        (await _fixture.CreateClient().GetAsync($"/api/v1/public/creators/nobody-{Guid.NewGuid():N}/albums"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ───── Helpers ─────

    private sealed record CreatorContext(HttpClient Client, string UserId, string Username, string Slug);

    private async Task<CreatorContext> SetupCreatorAsync()
    {
        var email = $"pubalbv1-{Guid.NewGuid():N}@test.com";
        const string password = "Test1234!@";
        await _fixture.RegisterUserAsync(email, password);
        var userId = await _fixture.GetUserIdAsync(email);
        var username = $"pav{Guid.NewGuid():N}"[..12];

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

        var slug = $"pav-{Guid.NewGuid():N}"[..20];
        (await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "public v1 album tests",
            showEarnings = false,
            showDownloadStats = false,
        })).EnsureSuccessStatusCode();

        return new CreatorContext(client, userId, username, slug);
    }

    private static async Task<JsonElement> CreateAlbumAsync(HttpClient client, object body)
    {
        var res = await client.PostAsJsonAsync("/api/v1/albums", body);
        var raw = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"album create failed ({(int)res.StatusCode}): {raw}");
        return JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("data");
    }

    private static async Task<List<string>> ReadAlbumIdsAsync(HttpResponseMessage res)
    {
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        return data.EnumerateArray().Select(a => a.GetProperty("id").GetString()!).ToList();
    }

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
            AudioUrl = "tracks/albumv1-leak-test.mp3",
            CreatorId = creatorUserId,
            Genre = "Electronic",
            Visibility = "public",
        });
        await db.SaveChangesAsync();
        return id;
    }
}
