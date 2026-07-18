using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests.V1;

/// <summary>
/// Integration tests for the versioned public lyrics endpoints:
/// - GET /api/v1/tracks/{trackId}/lyrics (public, visibility-checked)
/// - PUT /api/v1/tracks/{trackId}/lyrics (creator-owned)
/// These sit alongside the legacy /tracks/{id}/lyrics + /creator/tracks/{id}/lyrics
/// routes and share the same underlying TrackLyrics row/companion-table pattern:
/// edits never touch the Track row, so engagement (plays/sales) is unaffected.
/// </summary>
public sealed class TracksV1LyricsTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public TracksV1LyricsTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Create_ThenGet_RoundTripsLyricsAndIsExplicit()
    {
        var (client, userId) = await CreateCreatorAsync("v1lyr-create");
        var trackId = await _fixture.SeedTrackAsync(userId, "V1 Lyrics Beat");

        var putRes = await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "Verse one\nChorus shining bright",
            language = "en",
            isExplicit = true,
        });
        putRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var putEnvelope = await putRes.Content.ReadFromJsonAsync<JsonElement>();
        putEnvelope.GetProperty("success").GetBoolean().Should().BeTrue();
        var putData = putEnvelope.GetProperty("data");
        Guid.Parse(putData.GetProperty("trackId").GetString()!).Should().Be(trackId);
        putData.GetProperty("lyrics").GetString().Should().Be("Verse one\nChorus shining bright");
        putData.GetProperty("isExplicit").GetBoolean().Should().BeTrue();
        putData.GetProperty("version").GetInt32().Should().Be(1);

        using (var scope = _fixture.Services.CreateScope())
        {
            var freshDb = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var persisted = await freshDb.TrackLyrics.AsNoTracking().SingleAsync(x => x.TrackId == trackId);
            persisted.Lyrics.Should().Be("Verse one\nChorus shining bright");
            persisted.Version.Should().Be(1);
        }

        var anon = _fixture.CreateClient();
        var getRes = await anon.GetAsync($"/api/v1/tracks/{trackId}/lyrics");
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var getData = (await getRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        getData.GetProperty("lyrics").GetString().Should().Be("Verse one\nChorus shining bright");
        getData.GetProperty("isExplicit").GetBoolean().Should().BeTrue();

        var compliance = (await (await client.GetAsync($"/api/tracks/{trackId}/compliance-score"))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var lyricsItem = compliance.GetProperty("checklistItems").EnumerateArray()
            .Single(x => x.GetProperty("key").GetString() == "lyrics");
        lyricsItem.GetProperty("status").GetString().Should().Be("complete");
    }

    [Fact]
    public async Task Edit_UpdatesInPlace_KeepsSingleRow_AndDoesNotResetEngagement()
    {
        var (client, userId) = await CreateCreatorAsync("v1lyr-edit");
        var trackId = await _fixture.SeedTrackAsync(userId, "V1 Lyrics Edit Beat");
        await RecordPlaysAndSalesAsync(trackId, plays: 3, sales: 2);

        var createRes = await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "Original words",
        });
        createRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var version = (await createRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("version").GetInt32();

        var putRes = await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "Updated words",
            language = "pt-BR",
            isExplicit = false,
            version,
        });
        putRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await putRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("lyrics").GetString().Should().Be("Updated words");
        data.GetProperty("language").GetString().Should().Be("pt-BR");
        data.GetProperty("isExplicit").GetBoolean().Should().BeFalse();
        data.GetProperty("version").GetInt32().Should().Be(2);

        (await CountLyricsRowsAsync(trackId)).Should().Be(1);
        (await GetPlaysAndSalesAsync(trackId)).Should().Be((3, 2));
    }

    [Fact]
    public async Task NonOwnerCreator_Returns403_AnonymousPut_Returns401()
    {
        var (_, ownerUserId) = await CreateCreatorAsync("v1lyr-owner");
        var trackId = await _fixture.SeedTrackAsync(ownerUserId, "V1 Owned Beat");

        var (intruder, _) = await CreateCreatorAsync("v1lyr-intruder");
        var forbidden = await intruder.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "Not my track",
        });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var anon = _fixture.CreateClient();
        var unauthorized = await anon.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "No token",
        });
        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await CountLyricsRowsAsync(trackId)).Should().Be(0);
    }

    [Fact]
    public async Task PublicTrack_LyricsAreAnonymouslyReadable()
    {
        var (client, userId) = await CreateCreatorAsync("v1lyr-public");
        var trackId = await _fixture.SeedTrackAsync(userId, "V1 Public Beat", visibility: "public");

        (await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "Public lyrics content",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var anon = _fixture.CreateClient();
        var getRes = await anon.GetAsync($"/api/v1/tracks/{trackId}/lyrics");
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await getRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("lyrics").GetString().Should().Be("Public lyrics content");
    }

    [Fact]
    public async Task HiddenTrack_AnonGetReturns404_OwnerGetReturns200_NoLeak()
    {
        var (client, userId) = await CreateCreatorAsync("v1lyr-hidden");
        var trackId = await _fixture.SeedTrackAsync(userId, "V1 Hidden Beat", visibility: "hidden");

        (await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "Draft lyrics — must not leak",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Anonymous requester never learns this track (or its lyrics) exists.
        var anon = _fixture.CreateClient();
        var anonRes = await anon.GetAsync($"/api/v1/tracks/{trackId}/lyrics");
        anonRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var anonBody = await anonRes.Content.ReadAsStringAsync();
        anonBody.Should().NotContain("Draft lyrics");

        // Owner still sees their own hidden track's lyrics.
        var ownerRes = await client.GetAsync($"/api/v1/tracks/{trackId}/lyrics");
        ownerRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var ownerData = (await ownerRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        ownerData.GetProperty("lyrics").GetString().Should().Be("Draft lyrics — must not leak");
    }

    [Fact]
    public async Task ExplicitDelete_WithLatestVersion_DeletesRow_AndPublicGetReturns404()
    {
        var (client, userId) = await CreateCreatorAsync("v1lyr-empty");
        var trackId = await _fixture.SeedTrackAsync(userId, "V1 Empty Beat");

        var createRes = await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "Soon to be removed",
        });
        createRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var version = (await createRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("version").GetInt32();
        (await CountLyricsRowsAsync(trackId)).Should().Be(1);

        var deleteRes = await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            deleteLyrics = true,
            version,
        });
        deleteRes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountLyricsRowsAsync(trackId)).Should().Be(0);

        var anon = _fixture.CreateClient();
        var getRes = await anon.GetAsync($"/api/v1/tracks/{trackId}/lyrics");
        getRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StaleUpdateOrDelete_CannotOverwriteNewerLyrics()
    {
        var (client, userId) = await CreateCreatorAsync("v1lyr-stale");
        var trackId = await _fixture.SeedTrackAsync(userId, "V1 Stale Lyrics Beat");

        var create = await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "  Verse one\r\nChorus stays  ",
        });
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var version1 = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("version").GetInt32();

        var update = await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "Newer saved lyrics",
            version = version1,
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "Older overwrite",
            version = version1,
        })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            deleteLyrics = true,
            version = version1,
        })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var get = await client.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{trackId}/lyrics");
        get.GetProperty("data").GetProperty("lyrics").GetString().Should().Be("Newer saved lyrics");
        get.GetProperty("data").GetProperty("version").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task WhitespaceAndLineEndings_ArePersistedExactly()
    {
        var (client, userId) = await CreateCreatorAsync("v1lyr-whitespace");
        var trackId = await _fixture.SeedTrackAsync(userId, "V1 Whitespace Lyrics Beat");
        const string text = "  Verse one\r\n\r\nChorus with emoji 🎵  ";

        var put = await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new { lyrics = text });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("lyrics").GetString().Should().Be(text);

        using var scope = _fixture.Services.CreateScope();
        var freshDb = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        (await freshDb.TrackLyrics.AsNoTracking().SingleAsync(x => x.TrackId == trackId)).Lyrics.Should().Be(text);
    }

    [Fact]
    public async Task PublicTrackDto_NeverEmbedsRawLyrics()
    {
        var (client, userId) = await CreateCreatorAsync("v1lyr-dto");
        var trackId = await _fixture.SeedTrackAsync(userId, "V1 Dto Beat");

        (await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "Should never appear on the track DTO itself",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var anon = _fixture.CreateClient();
        var trackRes = await anon.GetAsync($"/api/v1/tracks/{trackId}");
        trackRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await trackRes.Content.ReadAsStringAsync();
        body.Should().NotContain("Should never appear on the track DTO itself");
    }

    // ────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────

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

    private async Task<int> CountLyricsRowsAsync(Guid trackId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return await db.TrackLyrics.CountAsync(l => l.TrackId == trackId);
    }

    private async Task RecordPlaysAndSalesAsync(Guid trackId, int plays, int sales)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        for (var i = 0; i < plays; i++)
        {
            db.StreamSessions.Add(new Cambrian.Domain.Entities.StreamSession
            {
                Id = Guid.NewGuid(),
                TrackId = trackId,
            });
        }

        for (var i = 0; i < sales; i++)
        {
            db.Purchases.Add(new Cambrian.Domain.Entities.Purchase
            {
                Id = Guid.NewGuid(),
                TrackId = trackId,
                BuyerId = $"buyer-{Guid.NewGuid():N}",
                Status = "completed",
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<(int Plays, int Sales)> GetPlaysAndSalesAsync(Guid trackId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var plays = await db.StreamSessions.CountAsync(s => s.TrackId == trackId);
        var sales = await db.Purchases.CountAsync(p => p.TrackId == trackId && p.Status == "completed");
        return (plays, sales);
    }
}
