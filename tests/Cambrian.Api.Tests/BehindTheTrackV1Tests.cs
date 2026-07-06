using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// Integration tests for the v1 "Behind The Track" surface:
/// - GET/PUT /api/v1/tracks/{trackId}/behind-the-track (process notes + public proof videos)
/// - POST/PATCH/DELETE /api/v1/tracks/{trackId}/proof-videos[/{videoId}]
/// Public reads respect the shared track visibility policy plus per-video
/// visibility; mutations require the owning creator's JWT.
/// </summary>
public sealed class BehindTheTrackV1Tests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public BehindTheTrackV1Tests(CambrianApiFixture fixture) => _fixture = fixture;

    // ────────────────────────────────────────────────────────────
    //  Process notes: save / edit
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveProcessNotes_OwnerPut_PersistsAllFields_AndAnonCanRead()
    {
        var (client, userId) = await CreateCreatorAsync("v1btt-save");
        var trackId = await _fixture.SeedTrackAsync(userId, "Save Notes Beat");

        var putRes = await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/behind-the-track", new
        {
            story = "Hummed the melody into a phone, built it up in the DAW.",
            daw = "Ableton Live 12",
            vocalChain = "Mic, Preamp, EQ, Compressor, Reverb",
            promptNotes = "verse: melancholic lo-fi piano loop, 85bpm",
            productionNotes = "Layered a live guitar over the AI stem for warmth.",
            humanContributionNotes = "Wrote all lyrics and arranged the final mix by hand.",
            youtubeUrl = "https://www.youtube.com/watch?v=abc12345678",
            toolsUsed = new[] { "Suno v5", "Ableton" },
        });
        putRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await putRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("story").GetString().Should().Be("Hummed the melody into a phone, built it up in the DAW.");
        data.GetProperty("daw").GetString().Should().Be("Ableton Live 12");
        data.GetProperty("vocalChain").GetString().Should().Be("Mic, Preamp, EQ, Compressor, Reverb");
        data.GetProperty("promptNotes").GetString().Should().Be("verse: melancholic lo-fi piano loop, 85bpm");
        data.GetProperty("productionNotes").GetString().Should().Be("Layered a live guitar over the AI stem for warmth.");
        data.GetProperty("humanContributionNotes").GetString().Should().Be("Wrote all lyrics and arranged the final mix by hand.");

        var anon = _fixture.CreateClient();
        var getRes = await anon.GetAsync($"/api/v1/tracks/{trackId}/behind-the-track");
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var getData = (await getRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        getData.GetProperty("daw").GetString().Should().Be("Ableton Live 12");
        getData.GetProperty("toolsUsed").GetArrayLength().Should().Be(2);
        getData.GetProperty("proofVideos").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetBehindTheTrack_NoDataYet_ReturnsEmptyPayload_NotFound()
    {
        var (_, userId) = await CreateCreatorAsync("v1btt-empty");
        var trackId = await _fixture.SeedTrackAsync(userId, "No Data Beat");

        var anon = _fixture.CreateClient();
        var res = await anon.GetAsync($"/api/v1/tracks/{trackId}/behind-the-track");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("story").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("toolsUsed").GetArrayLength().Should().Be(0);
        data.GetProperty("proofVideos").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task EditProcessNotes_SecondPut_UpdatesInPlace_PreservesCreatedAt()
    {
        var (client, userId) = await CreateCreatorAsync("v1btt-edit");
        var trackId = await _fixture.SeedTrackAsync(userId, "Edit Notes Beat");

        var firstPut = await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/behind-the-track", new
        {
            story = "Original story",
            daw = "FL Studio",
        });
        firstPut.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstData = (await firstPut.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var createdAt = firstData.GetProperty("createdAt").GetDateTime();

        var secondPut = await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/behind-the-track", new
        {
            story = "Revised story with more detail",
            daw = "FL Studio",
        });
        secondPut.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondData = (await secondPut.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        secondData.GetProperty("story").GetString().Should().Be("Revised story with more detail");
        secondData.GetProperty("createdAt").GetDateTime().Should().Be(createdAt);
        secondData.GetProperty("updatedAt").GetDateTime().Should().BeOnOrAfter(createdAt);

        // Editing companion data must never touch the Track row itself (engagement/identity untouched).
        var track = await GetTrackAsync(trackId);
        track.Title.Should().Be("Edit Notes Beat");
        track.Visibility.Should().Be("public");
        track.CreatorId.Should().Be(userId);
    }

    [Fact]
    public async Task ProcessNotes_NonOwner_Returns403_Anonymous_Returns401()
    {
        var (_, ownerUserId) = await CreateCreatorAsync("v1btt-owner");
        var trackId = await _fixture.SeedTrackAsync(ownerUserId, "Guarded Beat");

        var (intruder, _) = await CreateCreatorAsync("v1btt-intruder");
        var forbidden = await intruder.PutAsJsonAsync($"/api/v1/tracks/{trackId}/behind-the-track", new { story = "Not mine" });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var anon = _fixture.CreateClient();
        var unauthorized = await anon.PutAsJsonAsync($"/api/v1/tracks/{trackId}/behind-the-track", new { story = "No token" });
        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProcessNotes_HiddenTrack_AnonGet404_OwnerGet200()
    {
        var (client, userId) = await CreateCreatorAsync("v1btt-hidden");
        var trackId = await _fixture.SeedTrackAsync(userId, "Hidden Beat", visibility: "hidden");

        (await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/behind-the-track", new
        {
            story = "Draft story on a hidden track",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var anon = _fixture.CreateClient();
        (await anon.GetAsync($"/api/v1/tracks/{trackId}/behind-the-track")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var ownerRes = await client.GetAsync($"/api/v1/tracks/{trackId}/behind-the-track");
        ownerRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var ownerData = (await ownerRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        ownerData.GetProperty("story").GetString().Should().Be("Draft story on a hidden track");
    }

    // ────────────────────────────────────────────────────────────
    //  Proof videos: add / update / delete
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddProofVideo_Youtube_Succeeds_AndAnonCanReadItBackInBehindTheTrack()
    {
        var (client, userId) = await CreateCreatorAsync("v1pv-add");
        var trackId = await _fixture.SeedTrackAsync(userId, "Proof Video Beat");

        var postRes = await client.PostAsJsonAsync($"/api/v1/tracks/{trackId}/proof-videos", new
        {
            videoType = "YouTube",
            url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            title = "Studio session breakdown",
            description = "Screen recording of the full session.",
        });
        postRes.StatusCode.Should().Be(HttpStatusCode.Created);

        var data = (await postRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("videoType").GetString().Should().Be("YouTube");
        data.GetProperty("url").GetString().Should().Be("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        data.GetProperty("visibility").GetString().Should().Be("public");
        data.GetProperty("sortOrder").GetInt32().Should().Be(0);

        var anon = _fixture.CreateClient();
        var getRes = await anon.GetAsync($"/api/v1/tracks/{trackId}/behind-the-track");
        var proofVideos = (await getRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("proofVideos");
        proofVideos.GetArrayLength().Should().Be(1);
        proofVideos[0].GetProperty("title").GetString().Should().Be("Studio session breakdown");
    }

    [Fact]
    public async Task AddProofVideo_External_Succeeds()
    {
        var (client, userId) = await CreateCreatorAsync("v1pv-ext");
        var trackId = await _fixture.SeedTrackAsync(userId, "External Proof Beat");

        var res = await client.PostAsJsonAsync($"/api/v1/tracks/{trackId}/proof-videos", new
        {
            videoType = "External",
            url = "https://vimeo.com/123456789",
            title = "Vimeo walkthrough",
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("videoType").GetString().Should().Be("External");
        data.GetProperty("url").GetString().Should().Be("https://vimeo.com/123456789");
    }

    [Theory]
    [InlineData("YouTube", "https://www.youtube.com/@somechannel")]
    [InlineData("YouTube", "not a url at all")]
    [InlineData("YouTube", "https://vimeo.com/123456789")]
    [InlineData("External", "javascript:alert(1)")]
    [InlineData("External", "ftp://files.example.com/video.mp4")]
    [InlineData("External", "not a url")]
    public async Task AddProofVideo_InvalidUrl_Returns400_AndCreatesNoRow(string videoType, string url)
    {
        var (client, userId) = await CreateCreatorAsync("v1pv-badurl");
        var trackId = await _fixture.SeedTrackAsync(userId, "Bad Url Beat");

        var res = await client.PostAsJsonAsync($"/api/v1/tracks/{trackId}/proof-videos", new
        {
            videoType,
            url,
        });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountProofVideosAsync(trackId)).Should().Be(0);
    }

    [Fact]
    public async Task UpdateProofVideo_PartialPatch_ChangesOnlyProvidedFields_PreservesCreatedAt()
    {
        var (client, userId) = await CreateCreatorAsync("v1pv-update");
        var trackId = await _fixture.SeedTrackAsync(userId, "Update Proof Beat");

        var created = await CreateProofVideoAsync(client, trackId, url: "https://youtu.be/dQw4w9WgXcQ", title: "Original title");
        var videoId = created.GetProperty("id").GetString()!;
        var createdAt = created.GetProperty("createdAt").GetDateTime();
        var originalUrl = created.GetProperty("url").GetString();

        var patchRes = await client.PatchAsJsonAsync($"/api/v1/tracks/{trackId}/proof-videos/{videoId}", new
        {
            title = "Updated title",
        });
        patchRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = (await patchRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        updated.GetProperty("title").GetString().Should().Be("Updated title");
        updated.GetProperty("url").GetString().Should().Be(originalUrl);
        updated.GetProperty("videoType").GetString().Should().Be("YouTube");
        updated.GetProperty("createdAt").GetDateTime().Should().Be(createdAt);
        updated.GetProperty("updatedAt").GetDateTime().Should().BeOnOrAfter(createdAt);
    }

    [Fact]
    public async Task UpdateProofVideo_SwitchToInvalidExternalUrl_Returns400_LeavesRowUnchanged()
    {
        var (client, userId) = await CreateCreatorAsync("v1pv-switch");
        var trackId = await _fixture.SeedTrackAsync(userId, "Switch Proof Beat");
        var created = await CreateProofVideoAsync(client, trackId, url: "https://youtu.be/dQw4w9WgXcQ");
        var videoId = created.GetProperty("id").GetString()!;

        var patchRes = await client.PatchAsJsonAsync($"/api/v1/tracks/{trackId}/proof-videos/{videoId}", new
        {
            videoType = "External",
            url = "javascript:alert(1)",
        });
        patchRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var stillThere = await GetProofVideoEntityAsync(Guid.Parse(videoId));
        stillThere!.VideoType.Should().Be("YouTube");
        stillThere.Url.Should().Be("https://youtu.be/dQw4w9WgXcQ");
    }

    [Fact]
    public async Task DeleteProofVideo_Owner_RemovesRow_AndMissingIdReturns404()
    {
        var (client, userId) = await CreateCreatorAsync("v1pv-delete");
        var trackId = await _fixture.SeedTrackAsync(userId, "Delete Proof Beat");
        var created = await CreateProofVideoAsync(client, trackId, url: "https://youtu.be/dQw4w9WgXcQ");
        var videoId = created.GetProperty("id").GetString()!;

        var deleteRes = await client.DeleteAsync($"/api/v1/tracks/{trackId}/proof-videos/{videoId}");
        deleteRes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountProofVideosAsync(trackId)).Should().Be(0);

        var again = await client.DeleteAsync($"/api/v1/tracks/{trackId}/proof-videos/{videoId}");
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ProofVideo_NonOwner_Returns403_ForAddUpdateDelete()
    {
        var (owner, ownerUserId) = await CreateCreatorAsync("v1pv-owner");
        var trackId = await _fixture.SeedTrackAsync(ownerUserId, "Owner Only Proof Beat");
        var created = await CreateProofVideoAsync(owner, trackId, url: "https://youtu.be/dQw4w9WgXcQ");
        var videoId = created.GetProperty("id").GetString()!;

        var (intruder, _) = await CreateCreatorAsync("v1pv-intruder");

        (await intruder.PostAsJsonAsync($"/api/v1/tracks/{trackId}/proof-videos", new
        {
            videoType = "YouTube",
            url = "https://youtu.be/oHg5SJYRHA0",
        })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await intruder.PatchAsJsonAsync($"/api/v1/tracks/{trackId}/proof-videos/{videoId}", new
        {
            title = "Hijacked",
        })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await intruder.DeleteAsync($"/api/v1/tracks/{trackId}/proof-videos/{videoId}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await CountProofVideosAsync(trackId)).Should().Be(1);
    }

    // ────────────────────────────────────────────────────────────
    //  Public exposure rules
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublicView_HidesHiddenProofVideos_ButOwnerSeesAll()
    {
        var (client, userId) = await CreateCreatorAsync("v1pv-vis");
        var trackId = await _fixture.SeedTrackAsync(userId, "Mixed Visibility Beat");

        await CreateProofVideoAsync(client, trackId, url: "https://youtu.be/dQw4w9WgXcQ", title: "Public clip");
        await CreateProofVideoAsync(client, trackId, url: "https://youtu.be/oHg5SJYRHA0", title: "Draft clip", visibility: "hidden");

        var anon = _fixture.CreateClient();
        var anonRes = await anon.GetAsync($"/api/v1/tracks/{trackId}/behind-the-track");
        var anonVideos = (await anonRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("proofVideos");
        anonVideos.GetArrayLength().Should().Be(1);
        anonVideos[0].GetProperty("title").GetString().Should().Be("Public clip");

        var ownerRes = await client.GetAsync($"/api/v1/tracks/{trackId}/behind-the-track");
        var ownerVideos = (await ownerRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("proofVideos");
        ownerVideos.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task PublicView_HiddenTrack_HidesEverythingEvenPublicVideos()
    {
        var (client, userId) = await CreateCreatorAsync("v1pv-hiddentrack");
        var trackId = await _fixture.SeedTrackAsync(userId, "Hidden Track With Videos", visibility: "hidden");

        await CreateProofVideoAsync(client, trackId, url: "https://youtu.be/dQw4w9WgXcQ", title: "Would-be public clip");

        var anon = _fixture.CreateClient();
        var res = await anon.GetAsync($"/api/v1/tracks/{trackId}/behind-the-track");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────

    private async Task<JsonElement> CreateProofVideoAsync(
        HttpClient client, Guid trackId, string url, string videoType = "YouTube", string? title = null, string visibility = "public")
    {
        var res = await client.PostAsJsonAsync($"/api/v1/tracks/{trackId}/proof-videos", new
        {
            videoType,
            url,
            title,
            visibility,
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
    }

    private async Task<(HttpClient Client, string UserId)> CreateCreatorAsync(string emailPrefix)
    {
        var email = $"{emailPrefix}-{Guid.NewGuid():N}@test.com";
        var password = "Test1234!@";
        await _fixture.RegisterUserAsync(email, password);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email);
            user.Tier = "creator";
            user.Role = "Creator";
            var username = $"u{Guid.NewGuid():N}"[..14];
            user.UserName = username;
            user.NormalizedUserName = username.ToUpperInvariant();
            await db.SaveChangesAsync();
        }

        var token = await _fixture.LoginUserAsync(email, password);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var userId = await _fixture.GetUserIdAsync(email);
        return (client, userId);
    }

    private async Task<Cambrian.Domain.Entities.Track> GetTrackAsync(Guid trackId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return await db.Tracks.FirstAsync(t => t.Id == trackId);
    }

    private async Task<Cambrian.Domain.Entities.TrackVideoProof?> GetProofVideoEntityAsync(Guid videoId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return await db.TrackVideoProofs.AsNoTracking().FirstOrDefaultAsync(v => v.Id == videoId);
    }

    private async Task<int> CountProofVideosAsync(Guid trackId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return await db.TrackVideoProofs.CountAsync(v => v.TrackId == trackId);
    }
}
