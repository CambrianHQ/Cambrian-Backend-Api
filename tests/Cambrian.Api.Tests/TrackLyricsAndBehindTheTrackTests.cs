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
/// Integration tests for the track companion-row endpoints:
/// - PUT /creator/tracks/{id}/lyrics + public GET /tracks/{id}/lyrics
/// - PUT /creator/tracks/{id}/behind-the-track + public GET /tracks/{id}/behind-the-track
/// Both are 1:1 rows keyed by TrackId; empty upserts delete the row, public reads
/// respect the shared track visibility policy, and edits require ownership.
/// </summary>
public sealed class TrackLyricsAndBehindTheTrackTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public TrackLyricsAndBehindTheTrackTests(CambrianApiFixture fixture) => _fixture = fixture;

    // ────────────────────────────────────────────────────────────
    //  Lyrics
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lyrics_OwnerUpsert_DefaultsLanguageToEn_AndIsPubliclyReadable()
    {
        var (client, userId) = await CreateCreatorAsync("lyr-basic");
        var trackId = await _fixture.SeedTrackAsync(userId, "Lyrics Beat");

        var putRes = await client.PutAsJsonAsync($"/creator/tracks/{trackId}/lyrics", new
        {
            lyrics = "Verse one\nChorus shining bright",
        });
        putRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var envelope = await putRes.Content.ReadFromJsonAsync<JsonElement>();
        envelope.GetProperty("success").GetBoolean().Should().BeTrue();
        var data = envelope.GetProperty("data");
        Guid.Parse(data.GetProperty("trackId").GetString()!).Should().Be(trackId);
        data.GetProperty("lyrics").GetString().Should().Be("Verse one\nChorus shining bright");
        data.GetProperty("language").GetString().Should().Be("en");

        // Anonymous public read returns the same lyrics.
        var anon = _fixture.CreateClient();
        var getRes = await anon.GetAsync($"/tracks/{trackId}/lyrics");
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var getData = (await getRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        getData.GetProperty("lyrics").GetString().Should().Be("Verse one\nChorus shining bright");
        getData.GetProperty("language").GetString().Should().Be("en");
    }

    [Fact]
    public async Task Lyrics_SecondUpsert_UpdatesInPlace_KeepsSingleRow()
    {
        var (client, userId) = await CreateCreatorAsync("lyr-update");
        var trackId = await _fixture.SeedTrackAsync(userId, "Lyrics Update Beat");

        var createRes = await client.PutAsJsonAsync($"/creator/tracks/{trackId}/lyrics", new
        {
            lyrics = "Original words",
        });
        createRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var version = (await createRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("version").GetInt32();

        var putRes = await client.PutAsJsonAsync($"/creator/tracks/{trackId}/lyrics", new
        {
            lyrics = "Letra atualizada",
            language = "pt-BR",
            version,
        });
        putRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await putRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("lyrics").GetString().Should().Be("Letra atualizada");
        data.GetProperty("language").GetString().Should().Be("pt-BR");
        var createdAt = data.GetProperty("createdAt").GetDateTime();
        var updatedAt = data.GetProperty("updatedAt").GetDateTime();
        updatedAt.Should().BeOnOrAfter(createdAt);

        // Public read reflects the update.
        var anon = _fixture.CreateClient();
        var getRes = await anon.GetAsync($"/tracks/{trackId}/lyrics");
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var getData = (await getRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        getData.GetProperty("lyrics").GetString().Should().Be("Letra atualizada");
        getData.GetProperty("language").GetString().Should().Be("pt-BR");

        // Upsert must update in place — never accumulate rows.
        (await CountLyricsRowsAsync(trackId)).Should().Be(1);
    }

    [Fact]
    public async Task Lyrics_EmptyUpsert_DeletesRow_AndPublicGetReturns404()
    {
        var (client, userId) = await CreateCreatorAsync("lyr-delete");
        var trackId = await _fixture.SeedTrackAsync(userId, "Lyrics Delete Beat");

        var createRes = await client.PutAsJsonAsync($"/creator/tracks/{trackId}/lyrics", new
        {
            lyrics = "Soon to be removed",
        });
        createRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var version = (await createRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("version").GetInt32();
        (await CountLyricsRowsAsync(trackId)).Should().Be(1);

        // Removal is explicit and version-checked so stale empty writes cannot erase lyrics.
        var deleteRes = await client.PutAsJsonAsync($"/creator/tracks/{trackId}/lyrics", new
        {
            deleteLyrics = true,
            version,
        });
        deleteRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = await deleteRes.Content.ReadFromJsonAsync<JsonElement>();
        if (envelope.TryGetProperty("data", out var deletedData))
            deletedData.ValueKind.Should().Be(JsonValueKind.Null);

        (await CountLyricsRowsAsync(trackId)).Should().Be(0);

        var anon = _fixture.CreateClient();
        var getRes = await anon.GetAsync($"/tracks/{trackId}/lyrics");
        getRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lyrics_NonOwnerCreatorReturns403_AnonymousReturns401()
    {
        var (_, ownerUserId) = await CreateCreatorAsync("lyr-owner");
        var trackId = await _fixture.SeedTrackAsync(ownerUserId, "Owned Beat");

        // A different creator (passes the CanEditOwnTrack policy, fails ownership).
        var (intruder, _) = await CreateCreatorAsync("lyr-intruder");
        var forbidden = await intruder.PutAsJsonAsync($"/creator/tracks/{trackId}/lyrics", new
        {
            lyrics = "Not my track",
        });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Anonymous request never reaches the controller.
        var anon = _fixture.CreateClient();
        var unauthorized = await anon.PutAsJsonAsync($"/creator/tracks/{trackId}/lyrics", new
        {
            lyrics = "No token",
        });
        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await CountLyricsRowsAsync(trackId)).Should().Be(0);
    }

    [Fact]
    public async Task Lyrics_HiddenTrack_AnonGetReturns404_OwnerGetReturns200()
    {
        var (client, userId) = await CreateCreatorAsync("lyr-hidden");
        var trackId = await _fixture.SeedTrackAsync(userId, "Hidden Draft Beat");

        (await client.PutAsJsonAsync($"/creator/tracks/{trackId}/lyrics", new
        {
            lyrics = "Draft lyrics",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        await SetTrackVisibilityAsync(trackId, "hidden");

        // Hidden tracks are invisible to anonymous requesters — lyrics 404 with them.
        var anon = _fixture.CreateClient();
        var anonRes = await anon.GetAsync($"/tracks/{trackId}/lyrics");
        anonRes.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The owner still sees their own hidden track's lyrics.
        var ownerRes = await client.GetAsync($"/tracks/{trackId}/lyrics");
        ownerRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var ownerData = (await ownerRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        ownerData.GetProperty("lyrics").GetString().Should().Be("Draft lyrics");
    }

    [Fact]
    public async Task Lyrics_InvalidLanguageTag_Returns400()
    {
        var (client, userId) = await CreateCreatorAsync("lyr-lang");
        var trackId = await _fixture.SeedTrackAsync(userId, "Language Beat");

        var res = await client.PutAsJsonAsync($"/creator/tracks/{trackId}/lyrics", new
        {
            lyrics = "Words",
            language = "not a language!!",
        });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await CountLyricsRowsAsync(trackId)).Should().Be(0);
    }

    // ────────────────────────────────────────────────────────────
    //  Behind The Track
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task BehindTheTrack_OwnerUpsert_DedupesToolsCaseInsensitively_AndAnonCanRead()
    {
        var (client, userId) = await CreateCreatorAsync("btt-basic");
        var trackId = await _fixture.SeedTrackAsync(userId, "Process Beat");

        var putRes = await client.PutAsJsonAsync($"/creator/tracks/{trackId}/behind-the-track", new
        {
            story = "Started as a hummed voice memo, arranged in the DAW.",
            youtubeUrl = "https://www.youtube.com/watch?v=abc123",
            toolsUsed = new[] { "Suno v5", "Ableton", " suno v5 " },
        });
        putRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await putRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Guid.Parse(data.GetProperty("trackId").GetString()!).Should().Be(trackId);
        data.GetProperty("story").GetString().Should().Be("Started as a hummed voice memo, arranged in the DAW.");
        data.GetProperty("youtubeUrl").GetString().Should().Be("https://www.youtube.com/watch?v=abc123");

        // " suno v5 " trims + dedupes case-insensitively against "Suno v5".
        var tools = data.GetProperty("toolsUsed").EnumerateArray().Select(t => t.GetString()).ToList();
        tools.Should().BeEquivalentTo(new[] { "Suno v5", "Ableton" });

        var anon = _fixture.CreateClient();
        var getRes = await anon.GetAsync($"/tracks/{trackId}/behind-the-track");
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var getData = (await getRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        getData.GetProperty("story").GetString().Should().Be("Started as a hummed voice memo, arranged in the DAW.");
        getData.GetProperty("youtubeUrl").GetString().Should().Be("https://www.youtube.com/watch?v=abc123");
        getData.GetProperty("toolsUsed").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task BehindTheTrack_NonYoutubeUrlReturns400_YoutuBeShortLinkAccepted()
    {
        var (client, userId) = await CreateCreatorAsync("btt-url");
        var trackId = await _fixture.SeedTrackAsync(userId, "Url Beat");

        var bad = await client.PutAsJsonAsync($"/creator/tracks/{trackId}/behind-the-track", new
        {
            youtubeUrl = "https://vimeo.com/123",
        });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var good = await client.PutAsJsonAsync($"/creator/tracks/{trackId}/behind-the-track", new
        {
            youtubeUrl = "https://youtu.be/xyz",
        });
        good.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await good.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("youtubeUrl").GetString().Should().Be("https://youtu.be/xyz");
    }

    [Fact]
    public async Task BehindTheTrack_AllEmptyUpsert_DeletesRow_AndPublicGetReturns404()
    {
        var (client, userId) = await CreateCreatorAsync("btt-delete");
        var trackId = await _fixture.SeedTrackAsync(userId, "Removable Process Beat");

        (await client.PutAsJsonAsync($"/creator/tracks/{trackId}/behind-the-track", new
        {
            story = "Temporary story",
            toolsUsed = new[] { "Suno v5" },
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountBehindTheTrackRowsAsync(trackId)).Should().Be(1);

        // All fields empty → the row is removed entirely.
        var deleteRes = await client.PutAsJsonAsync($"/creator/tracks/{trackId}/behind-the-track", new
        {
            story = (string?)null,
            youtubeUrl = "",
            toolsUsed = Array.Empty<string>(),
        });
        deleteRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = await deleteRes.Content.ReadFromJsonAsync<JsonElement>();
        if (envelope.TryGetProperty("data", out var deletedData))
            deletedData.ValueKind.Should().Be(JsonValueKind.Null);

        (await CountBehindTheTrackRowsAsync(trackId)).Should().Be(0);

        var anon = _fixture.CreateClient();
        var getRes = await anon.GetAsync($"/tracks/{trackId}/behind-the-track");
        getRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BehindTheTrack_NonOwnerCreator_Returns403()
    {
        var (_, ownerUserId) = await CreateCreatorAsync("btt-owner");
        var trackId = await _fixture.SeedTrackAsync(ownerUserId, "Guarded Process Beat");

        var (intruder, _) = await CreateCreatorAsync("btt-intruder");
        var res = await intruder.PutAsJsonAsync($"/creator/tracks/{trackId}/behind-the-track", new
        {
            story = "Trying to rewrite someone else's story",
        });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await CountBehindTheTrackRowsAsync(trackId)).Should().Be(0);
    }

    [Fact]
    public async Task BehindTheTrack_ToolNameOver100Chars_Returns400()
    {
        var (client, userId) = await CreateCreatorAsync("btt-toolen");
        var trackId = await _fixture.SeedTrackAsync(userId, "Tool Length Beat");

        var res = await client.PutAsJsonAsync($"/creator/tracks/{trackId}/behind-the-track", new
        {
            toolsUsed = new[] { new string('x', 101) },
        });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await CountBehindTheTrackRowsAsync(trackId)).Should().Be(0);
    }

    // ────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Register a user, promote them to a fully-onboarded creator (tier, role, username —
    /// CreatorController carries a class-level [RequireUsername]), re-login for a fresh JWT,
    /// and return the authenticated client plus the user id for track seeding.
    /// </summary>
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

        // Re-login so the JWT carries the Creator role claim.
        var token = await _fixture.LoginUserAsync(email, password);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var userId = await _fixture.GetUserIdAsync(email);
        return (client, userId);
    }

    private async Task SetTrackVisibilityAsync(Guid trackId, string visibility)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var track = await db.Tracks.FirstAsync(t => t.Id == trackId);
        track.Visibility = visibility;
        await db.SaveChangesAsync();
    }

    private async Task<int> CountLyricsRowsAsync(Guid trackId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return await db.TrackLyrics.CountAsync(l => l.TrackId == trackId);
    }

    private async Task<int> CountBehindTheTrackRowsAsync(Guid trackId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return await db.TrackCreationProcesses.CountAsync(p => p.TrackId == trackId);
    }
}
