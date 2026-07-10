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
/// Authenticated album management over the versioned REST surface
/// (<c>/api/v1/albums</c>): create/update/delete, add/remove/reorder tracks,
/// ownership enforcement, and the core invariant that album track operations
/// never mutate or delete the underlying Track rows.
/// </summary>
[Trait("Category", "Critical")]
public sealed class AlbumsV1Tests : IClassFixture<CambrianApiFixture>
{
    private const string AlbumsUrl = "/api/v1/albums";

    private readonly CambrianApiFixture _fixture;

    public AlbumsV1Tests(CambrianApiFixture fixture) => _fixture = fixture;

    // ───── Create ─────

    [Fact]
    public async Task Create_WithTracks_Returns201WithOrderedJoinRows()
    {
        var (client, userId) = await CreateCreatorClientAsync();
        var trackA = await _fixture.SeedTrackAsync(userId, "Track A");
        var trackB = await _fixture.SeedTrackAsync(userId, "Track B");

        var data = await CreateAlbumAsync(client, new
        {
            title = "My V1 Album",
            description = "Debut",
            trackIds = new[] { trackA.ToString(), trackB.ToString() },
        });

        data.GetProperty("slug").GetString().Should().Be("my-v1-album");
        data.GetProperty("visibility").GetString().Should().Be("public");
        data.GetProperty("creatorId").GetString().Should().Be(userId);
        data.GetProperty("trackCount").GetInt32().Should().Be(2);
        ReadTrackIds(data).Should().Equal(trackA, trackB);

        var albumId = Guid.Parse(data.GetProperty("id").GetString()!);
        var rows = await GetAlbumTrackRowsAsync(albumId);
        rows.Select(r => r.TrackId).Should().Equal(trackA, trackB);
        rows.Select(r => r.Position).Should().Equal(0, 1);
    }

    [Fact]
    public async Task Create_WithForeignTrack_Returns400()
    {
        var (client, _) = await CreateCreatorClientAsync();
        var otherEmail = $"albv1-other-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(otherEmail);
        var otherUserId = await _fixture.GetUserIdAsync(otherEmail);
        var foreignTrack = await _fixture.SeedTrackAsync(otherUserId, "Not Yours");

        var res = await client.PostAsJsonAsync(AlbumsUrl, new
        {
            title = "Stolen",
            trackIds = new[] { foreignTrack.ToString() },
        });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_InvalidVisibility_Returns400()
    {
        var (client, _) = await CreateCreatorClientAsync();
        var res = await client.PostAsJsonAsync(AlbumsUrl, new { title = "Bad Vis", visibility = "friends-only" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_AcceptsAllFourVisibilities()
    {
        var (client, _) = await CreateCreatorClientAsync();
        foreach (var vis in new[] { "draft", "public", "unlisted", "private" })
        {
            var data = await CreateAlbumAsync(client, new { title = $"Vis {vis}", visibility = vis });
            data.GetProperty("visibility").GetString().Should().Be(vis);
        }
    }

    // ───── AuthZ ─────

    [Fact]
    public async Task Create_Anonymous401_NonCreator403()
    {
        var anon = _fixture.CreateClient();
        (await anon.PostAsJsonAsync(AlbumsUrl, new { title = "Anon" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var fan = await _fixture.CreateAuthenticatedClientAsync($"albv1-fan-{Guid.NewGuid():N}@test.com");
        (await fan.PostAsJsonAsync(AlbumsUrl, new { title = "Fan" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListMyAlbums_ReturnsOwnAlbumsIncludingDrafts()
    {
        var (client, _) = await CreateCreatorClientAsync();
        var draft = await CreateAlbumAsync(client, new { title = "Draft One", visibility = "draft" });
        var draftId = draft.GetProperty("id").GetString()!;

        var res = await client.GetAsync(AlbumsUrl);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.EnumerateArray().Select(a => a.GetProperty("id").GetString()).Should().Contain(draftId);
    }

    // ───── Update ─────

    [Fact]
    public async Task Update_RenameKeepsSlugStable_AndDoesNotTouchTracks()
    {
        var (client, userId) = await CreateCreatorClientAsync();
        var trackA = await _fixture.SeedTrackAsync(userId, "Track A");
        var created = await CreateAlbumAsync(client, new
        {
            title = "Original",
            trackIds = new[] { trackA.ToString() },
        });
        var albumId = created.GetProperty("id").GetString()!;
        created.GetProperty("slug").GetString().Should().Be("original");

        var res = await client.PatchAsJsonAsync($"{AlbumsUrl}/{albumId}", new { title = "Renamed", visibility = "unlisted" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("title").GetString().Should().Be("Renamed");
        data.GetProperty("slug").GetString().Should().Be("original");
        data.GetProperty("visibility").GetString().Should().Be("unlisted");
        // Metadata PATCH must not disturb membership.
        ReadTrackIds(data).Should().Equal(trackA);
    }

    [Fact]
    public async Task Update_NonOwner403_Unknown404()
    {
        var (owner, _) = await CreateCreatorClientAsync();
        var (intruder, _) = await CreateCreatorClientAsync();
        var created = await CreateAlbumAsync(owner, new { title = "Owned" });
        var albumId = created.GetProperty("id").GetString()!;

        (await intruder.PatchAsJsonAsync($"{AlbumsUrl}/{albumId}", new { title = "Hijack" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.PatchAsJsonAsync($"{AlbumsUrl}/{Guid.NewGuid()}", new { title = "Ghost" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ───── Delete ─────

    [Fact]
    public async Task Delete_Returns204_RemovesJoinRowsButNeverTracks()
    {
        var (client, userId) = await CreateCreatorClientAsync();
        var trackA = await _fixture.SeedTrackAsync(userId, "Survivor A");
        var trackB = await _fixture.SeedTrackAsync(userId, "Survivor B");
        var created = await CreateAlbumAsync(client, new
        {
            title = "Doomed",
            trackIds = new[] { trackA.ToString(), trackB.ToString() },
        });
        var albumId = Guid.Parse(created.GetProperty("id").GetString()!);

        (await client.DeleteAsync($"{AlbumsUrl}/{albumId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        (await db.AlbumTracks.AnyAsync(at => at.AlbumId == albumId)).Should().BeFalse();
        (await db.Tracks.CountAsync(t => t.Id == trackA || t.Id == trackB)).Should().Be(2);
    }

    // ───── Add tracks ─────

    [Fact]
    public async Task AddTracks_AppendsAndPreservesExistingAddedAt()
    {
        var (client, userId) = await CreateCreatorClientAsync();
        var trackA = await _fixture.SeedTrackAsync(userId, "A");
        var trackB = await _fixture.SeedTrackAsync(userId, "B");
        var created = await CreateAlbumAsync(client, new { title = "Growing", trackIds = new[] { trackA.ToString() } });
        var albumId = Guid.Parse(created.GetProperty("id").GetString()!);

        var before = await GetAlbumTrackRowsAsync(albumId);
        var originalAddedAt = before.Single(r => r.TrackId == trackA).AddedAt;

        var res = await client.PostAsJsonAsync($"{AlbumsUrl}/{albumId}/tracks", new { trackIds = new[] { trackB.ToString() } });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        ReadTrackIds(data).Should().Equal(trackA, trackB);

        var after = await GetAlbumTrackRowsAsync(albumId);
        after.Select(r => r.TrackId).Should().Equal(trackA, trackB);
        after.Single(r => r.TrackId == trackA).AddedAt.Should().Be(originalAddedAt, "retained rows keep their AddedAt");
    }

    [Fact]
    public async Task AddTracks_ForeignTrack_Returns400()
    {
        var (client, userId) = await CreateCreatorClientAsync();
        var created = await CreateAlbumAsync(client, new { title = "Album" });
        var albumId = created.GetProperty("id").GetString()!;

        var otherEmail = $"albv1-add-other-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(otherEmail);
        var foreignTrack = await _fixture.SeedTrackAsync(await _fixture.GetUserIdAsync(otherEmail), "Nope");

        (await client.PostAsJsonAsync($"{AlbumsUrl}/{albumId}/tracks", new { trackIds = new[] { foreignTrack.ToString() } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ───── Remove track ─────

    [Fact]
    public async Task RemoveTrack_RemovesJoinRowButNotTrack()
    {
        var (client, userId) = await CreateCreatorClientAsync();
        var trackA = await _fixture.SeedTrackAsync(userId, "Keep");
        var trackB = await _fixture.SeedTrackAsync(userId, "Remove");
        var created = await CreateAlbumAsync(client, new
        {
            title = "Trim Me",
            trackIds = new[] { trackA.ToString(), trackB.ToString() },
        });
        var albumId = Guid.Parse(created.GetProperty("id").GetString()!);

        var res = await client.DeleteAsync($"{AlbumsUrl}/{albumId}/tracks/{trackB}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        ReadTrackIds(data).Should().Equal(trackA);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        (await db.AlbumTracks.AnyAsync(at => at.AlbumId == albumId && at.TrackId == trackB)).Should().BeFalse();
        (await db.Tracks.AnyAsync(t => t.Id == trackB)).Should().BeTrue("removing a track from an album must never delete the track");
    }

    [Fact]
    public async Task RemoveTrack_NotInAlbum_Returns404()
    {
        var (client, userId) = await CreateCreatorClientAsync();
        var created = await CreateAlbumAsync(client, new { title = "Empty" });
        var albumId = created.GetProperty("id").GetString()!;

        (await client.DeleteAsync($"{AlbumsUrl}/{albumId}/tracks/{Guid.NewGuid()}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ───── Reorder ─────

    [Fact]
    public async Task Reorder_UpdatesPositions_PreservesAddedAt()
    {
        var (client, userId) = await CreateCreatorClientAsync();
        var trackA = await _fixture.SeedTrackAsync(userId, "A");
        var trackB = await _fixture.SeedTrackAsync(userId, "B");
        var created = await CreateAlbumAsync(client, new
        {
            title = "Reorder Me",
            trackIds = new[] { trackA.ToString(), trackB.ToString() },
        });
        var albumId = Guid.Parse(created.GetProperty("id").GetString()!);
        var addedAtByTrack = (await GetAlbumTrackRowsAsync(albumId)).ToDictionary(r => r.TrackId, r => r.AddedAt);

        var res = await client.PatchAsJsonAsync($"{AlbumsUrl}/{albumId}/tracks/reorder",
            new { trackIds = new[] { trackB.ToString(), trackA.ToString() } });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        ReadTrackIds(data).Should().Equal(trackB, trackA);

        var after = await GetAlbumTrackRowsAsync(albumId);
        after.Select(r => r.TrackId).Should().Equal(trackB, trackA);
        after.Select(r => r.Position).Should().Equal(0, 1);
        after.Single(r => r.TrackId == trackA).AddedAt.Should().Be(addedAtByTrack[trackA]);
    }

    [Fact]
    public async Task Reorder_NonPermutation_Returns400()
    {
        var (client, userId) = await CreateCreatorClientAsync();
        var trackA = await _fixture.SeedTrackAsync(userId, "A");
        var trackB = await _fixture.SeedTrackAsync(userId, "B");
        var created = await CreateAlbumAsync(client, new { title = "Perm", trackIds = new[] { trackA.ToString() } });
        var albumId = created.GetProperty("id").GetString()!;

        // trackB is not a member — reorder must not be able to add it.
        (await client.PatchAsJsonAsync($"{AlbumsUrl}/{albumId}/tracks/reorder",
            new { trackIds = new[] { trackA.ToString(), trackB.ToString() } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ───── Preservation: existing public track routes still work ─────

    [Fact]
    public async Task ExistingTrack_StillResolvableAfterAlbumChurn()
    {
        var (client, userId) = await CreateCreatorClientAsync();
        var track = await _fixture.SeedTrackAsync(userId, "Durable");
        var created = await CreateAlbumAsync(client, new { title = "Churn", trackIds = new[] { track.ToString() } });
        var albumId = created.GetProperty("id").GetString()!;

        await client.DeleteAsync($"{AlbumsUrl}/{albumId}/tracks/{track}");
        await client.DeleteAsync($"{AlbumsUrl}/{albumId}");

        // Public track lookup still returns the untouched track.
        var res = await _fixture.CreateClient().GetAsync($"/api/v1/tracks/{track}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ───── Helpers ─────

    private async Task<(HttpClient Client, string UserId)> CreateCreatorClientAsync()
    {
        var email = $"albv1-{Guid.NewGuid():N}@test.com";
        const string password = "Test1234!@";
        await _fixture.RegisterUserAsync(email, password);
        await _fixture.SetUserRoleAsync(email, "Creator");
        await _fixture.SetCreatorTierAsync(email, Cambrian.Domain.Enums.CreatorTier.Creator);
        await _fixture.SetUsernameAsync(email, $"u{Guid.NewGuid():N}"[..12]);
        await _fixture.SetEmailVerifiedAsync(email, true);

        var token = await _fixture.LoginUserAsync(email, password);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, await _fixture.GetUserIdAsync(email));
    }

    private static async Task<JsonElement> CreateAlbumAsync(HttpClient client, object body)
    {
        var res = await client.PostAsJsonAsync(AlbumsUrl, body);
        var raw = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"album create failed ({(int)res.StatusCode}): {raw}");
        var json = JsonSerializer.Deserialize<JsonElement>(raw);
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        return json.GetProperty("data");
    }

    private static List<Guid> ReadTrackIds(JsonElement data) =>
        data.GetProperty("trackIds").EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).ToList();

    private async Task<List<AlbumTrack>> GetAlbumTrackRowsAsync(Guid albumId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return await db.AlbumTracks.AsNoTracking()
            .Where(at => at.AlbumId == albumId).OrderBy(at => at.Position).ToListAsync();
    }
}
