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

        var anon = _fixture.CreateClient();
        var getRes = await anon.GetAsync($"/api/v1/tracks/{trackId}/lyrics");
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var getData = (await getRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        getData.GetProperty("lyrics").GetString().Should().Be("Verse one\nChorus shining bright");
        getData.GetProperty("isExplicit").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Edit_UpdatesInPlace_KeepsSingleRow_AndDoesNotResetEngagement()
    {
        var (client, userId) = await CreateCreatorAsync("v1lyr-edit");
        var trackId = await _fixture.SeedTrackAsync(userId, "V1 Lyrics Edit Beat");
        await RecordPlaysAndSalesAsync(trackId, plays: 3, sales: 2);

        (await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "Original words",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var putRes = await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "Updated words",
            language = "pt-BR",
            isExplicit = false,
        });
        putRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await putRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("lyrics").GetString().Should().Be("Updated words");
        data.GetProperty("language").GetString().Should().Be("pt-BR");
        data.GetProperty("isExplicit").GetBoolean().Should().BeFalse();

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
    public async Task EmptyLyrics_DeletesRow_AndPublicGetReturns404()
    {
        var (client, userId) = await CreateCreatorAsync("v1lyr-empty");
        var trackId = await _fixture.SeedTrackAsync(userId, "V1 Empty Beat");

        (await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "Soon to be removed",
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountLyricsRowsAsync(trackId)).Should().Be(1);

        var deleteRes = await client.PutAsJsonAsync($"/api/v1/tracks/{trackId}/lyrics", new
        {
            lyrics = "   ",
        });
        deleteRes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountLyricsRowsAsync(trackId)).Should().Be(0);

        var anon = _fixture.CreateClient();
        var getRes = await anon.GetAsync($"/api/v1/tracks/{trackId}/lyrics");
        getRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
                IdempotencyKey = Guid.NewGuid().ToString(),
                Qualified = true,
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

        // Plays are read from the TrackStats projection, not a live COUNT — rebuild it from
        // the events just seeded.
        var reconciliation = scope.ServiceProvider.GetRequiredService<Cambrian.Application.Interfaces.IPlayCountReconciliationService>();
        await reconciliation.ReconcileAsync(new Cambrian.Application.DTOs.PlayCounts.ReconciliationOptions
        {
            TrackIds = new[] { trackId },
            DryRun = false,
            Repair = true,
        });
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
