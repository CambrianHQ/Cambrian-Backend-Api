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
/// Album (TrackCollection) CRUD over HTTP: create/update/delete with slug generation,
/// visibility + releaseDate validation, track-ownership enforcement, join-row
/// (AlbumTracks) persistence, and cover upload. Albums are relationships only —
/// deleting an album must never delete Track rows.
/// </summary>
[Trait("Category", "Critical")]
public sealed class AlbumCrudTests : IClassFixture<CambrianApiFixture>
{
    private const string CollectionsUrl = "/creator-profile/me/collections";

    private static readonly byte[] TinyPngBytes =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
    };

    private readonly CambrianApiFixture _fixture;

    public AlbumCrudTests(CambrianApiFixture fixture) => _fixture = fixture;

    // ───── 1. Create ─────

    [Fact]
    public async Task CreateAlbum_WithTitleDescriptionAndTracks_Returns201WithOrderedJoinRows()
    {
        var (client, userId) = await CreateCreatorClientAsync();
        var trackA = await _fixture.SeedTrackAsync(userId, "Track A");
        var trackB = await _fixture.SeedTrackAsync(userId, "Track B");

        var data = await CreateAlbumAsync(client, new
        {
            title = "My First Album",
            description = "Debut collection",
            trackIds = $"{trackA},{trackB}",
        });

        data.GetProperty("slug").GetString().Should().Be("my-first-album");
        data.GetProperty("visibility").GetString().Should().Be("public");
        data.GetProperty("description").GetString().Should().Be("Debut collection");
        ReadTrackIds(data).Should().Equal(trackA, trackB);

        var albumId = Guid.Parse(data.GetProperty("id").GetString()!);
        var rows = await GetAlbumTrackRowsAsync(albumId);
        rows.Should().HaveCount(2);
        rows[0].TrackId.Should().Be(trackA);
        rows[0].Position.Should().Be(0);
        rows[1].TrackId.Should().Be(trackB);
        rows[1].Position.Should().Be(1);
    }

    [Fact]
    public async Task CreateAlbum_DuplicateTitleBySameCreator_GetsSuffixedSlug()
    {
        var (client, _) = await CreateCreatorClientAsync();

        var first = await CreateAlbumAsync(client, new { title = "Night Drives", trackIds = "" });
        var second = await CreateAlbumAsync(client, new { title = "Night Drives", trackIds = "" });

        first.GetProperty("slug").GetString().Should().Be("night-drives");
        second.GetProperty("slug").GetString().Should().Be("night-drives-2");
    }

    [Fact]
    public async Task CreateAlbum_PrivateWithReleaseDate_EchoesBothInDto()
    {
        var (client, _) = await CreateCreatorClientAsync();

        var data = await CreateAlbumAsync(client, new
        {
            title = "Hidden Preview",
            trackIds = "",
            visibility = "private",
            releaseDate = "2026-09-01T00:00:00Z",
        });

        data.GetProperty("visibility").GetString().Should().Be("private");
        data.GetProperty("releaseDate").GetDateTime().Date.Should().Be(new DateTime(2026, 9, 1));
    }

    [Fact]
    public async Task CreateAlbum_InvalidVisibilityOrReleaseDate_Returns400()
    {
        var (client, _) = await CreateCreatorClientAsync();

        var badVisibility = await client.PostAsJsonAsync(CollectionsUrl, new
        {
            title = "Bad Visibility",
            trackIds = "",
            visibility = "friends-only",
        });
        badVisibility.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var badDate = await client.PostAsJsonAsync(CollectionsUrl, new
        {
            title = "Bad Date",
            trackIds = "",
            releaseDate = "not-a-date",
        });
        badDate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateAlbum_WithTrackOwnedByAnotherCreator_Returns400()
    {
        var (client, _) = await CreateCreatorClientAsync();

        var otherEmail = $"album-other-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(otherEmail);
        var otherUserId = await _fixture.GetUserIdAsync(otherEmail);
        var foreignTrack = await _fixture.SeedTrackAsync(otherUserId, "Not Yours");

        var res = await client.PostAsJsonAsync(CollectionsUrl, new
        {
            title = "Stolen Goods",
            trackIds = foreignTrack.ToString(),
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ───── 2. Update ─────

    [Fact]
    public async Task UpdateAlbum_ReorderTracks_UpdatesPositionsAndPreservesAddedAt()
    {
        var (client, userId) = await CreateCreatorClientAsync();
        var trackA = await _fixture.SeedTrackAsync(userId, "Track A");
        var trackB = await _fixture.SeedTrackAsync(userId, "Track B");

        var created = await CreateAlbumAsync(client, new
        {
            title = "Reorder Me",
            trackIds = $"{trackA},{trackB}",
        });
        var albumId = Guid.Parse(created.GetProperty("id").GetString()!);

        var before = await GetAlbumTrackRowsAsync(albumId);
        var addedAtByTrack = before.ToDictionary(r => r.TrackId, r => r.AddedAt);

        var updateRes = await client.PutAsJsonAsync($"{CollectionsUrl}/{albumId}", new
        {
            trackIds = $"{trackB},{trackA}",
        });
        updateRes.EnsureSuccessStatusCode();
        var updated = (await updateRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        ReadTrackIds(updated).Should().Equal(trackB, trackA);

        var after = await GetAlbumTrackRowsAsync(albumId);
        after.Should().HaveCount(2);
        after[0].TrackId.Should().Be(trackB);
        after[0].Position.Should().Be(0);
        after[1].TrackId.Should().Be(trackA);
        after[1].Position.Should().Be(1);

        // Retained rows keep their original AddedAt — reorders must not recreate rows.
        after[0].AddedAt.Should().Be(addedAtByTrack[trackB]);
        after[1].AddedAt.Should().Be(addedAtByTrack[trackA]);
    }

    [Fact]
    public async Task UpdateAlbum_RenameTitle_KeepsSlugStable()
    {
        var (client, _) = await CreateCreatorClientAsync();

        var created = await CreateAlbumAsync(client, new { title = "Original Name", trackIds = "" });
        var albumId = created.GetProperty("id").GetString()!;
        created.GetProperty("slug").GetString().Should().Be("original-name");

        var updateRes = await client.PutAsJsonAsync($"{CollectionsUrl}/{albumId}", new
        {
            title = "Renamed Completely",
        });
        updateRes.EnsureSuccessStatusCode();
        var updated = (await updateRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        updated.GetProperty("title").GetString().Should().Be("Renamed Completely");
        updated.GetProperty("slug").GetString().Should().Be("original-name");
    }

    [Fact]
    public async Task UpdateAlbum_NonOwnerReturns403_UnknownIdReturns404()
    {
        var (owner, _) = await CreateCreatorClientAsync();
        var (intruder, _) = await CreateCreatorClientAsync();

        var created = await CreateAlbumAsync(owner, new { title = "Owned Album", trackIds = "" });
        var albumId = created.GetProperty("id").GetString()!;

        var forbidden = await intruder.PutAsJsonAsync($"{CollectionsUrl}/{albumId}", new
        {
            title = "Hijacked",
        });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var missing = await owner.PutAsJsonAsync($"{CollectionsUrl}/{Guid.NewGuid()}", new
        {
            title = "Ghost Album",
        });
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ───── 3. AuthZ on create ─────

    [Fact]
    public async Task CreateAlbum_AnonymousReturns401_NonCreatorTierReturns403()
    {
        var anon = _fixture.CreateClient();
        var anonRes = await anon.PostAsJsonAsync(CollectionsUrl, new { title = "Anon Album", trackIds = "" });
        anonRes.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var fan = await _fixture.CreateAuthenticatedClientAsync($"album-fan-{Guid.NewGuid():N}@test.com");
        var fanRes = await fan.PostAsJsonAsync(CollectionsUrl, new { title = "Fan Album", trackIds = "" });
        fanRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ───── 4. Delete ─────

    [Fact]
    public async Task DeleteAlbum_Returns204_RemovesJoinRowsButNeverTracks()
    {
        var (client, userId) = await CreateCreatorClientAsync();
        var trackA = await _fixture.SeedTrackAsync(userId, "Survivor A");
        var trackB = await _fixture.SeedTrackAsync(userId, "Survivor B");

        var created = await CreateAlbumAsync(client, new
        {
            title = "Doomed Album",
            trackIds = $"{trackA},{trackB}",
        });
        var albumId = Guid.Parse(created.GetProperty("id").GetString()!);
        (await GetAlbumTrackRowsAsync(albumId)).Should().HaveCount(2);

        var deleteRes = await client.DeleteAsync($"{CollectionsUrl}/{albumId}");
        deleteRes.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        (await db.AlbumTracks.AnyAsync(at => at.AlbumId == albumId)).Should().BeFalse();

        var survivors = await db.Tracks.AsNoTracking()
            .Where(t => t.Id == trackA || t.Id == trackB)
            .ToListAsync();
        survivors.Should().HaveCount(2, "deleting an album must never delete the tracks themselves");
    }

    // ───── 5. Cover upload ─────

    [Fact]
    public async Task UploadAlbumCover_ValidPngReturns200WithUrl_NonOwnerReturns403()
    {
        var (owner, _) = await CreateCreatorClientAsync();
        var created = await CreateAlbumAsync(owner, new { title = "Cover Album", trackIds = "" });
        var albumId = created.GetProperty("id").GetString()!;

        using var form = CreatePngUploadForm();
        var res = await owner.PostAsync($"{CollectionsUrl}/{albumId}/cover", form);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("coverImageUrl").GetString().Should().NotBeNullOrEmpty();

        var (intruder, _) = await CreateCreatorClientAsync();
        using var intruderForm = CreatePngUploadForm();
        var forbidden = await intruder.PostAsync($"{CollectionsUrl}/{albumId}/cover", intruderForm);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ───── Helpers ─────

    /// <summary>
    /// Register a user, promote to creator tier with a username (CreatorController-style
    /// prerequisites), and re-login so the JWT carries the Creator role claim.
    /// </summary>
    private async Task<(HttpClient Client, string UserId)> CreateCreatorClientAsync()
    {
        var email = $"album-crud-{Guid.NewGuid():N}@test.com";
        const string password = "Test1234!@";

        await _fixture.RegisterUserAsync(email, password);
        await _fixture.SetUserRoleAsync(email, "Creator");
        await _fixture.SetCreatorTierAsync(email, Cambrian.Domain.Enums.CreatorTier.Creator);
        await _fixture.SetUsernameAsync(email, $"u{Guid.NewGuid():N}"[..12]);
        await _fixture.SetEmailVerifiedAsync(email, true);

        var token = await _fixture.LoginUserAsync(email, password);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var userId = await _fixture.GetUserIdAsync(email);
        return (client, userId);
    }

    /// <summary>POST an album and return the unwrapped `data` element (asserts 201).</summary>
    private static async Task<JsonElement> CreateAlbumAsync(HttpClient client, object body)
    {
        var res = await client.PostAsJsonAsync(CollectionsUrl, body);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        return json.GetProperty("data");
    }

    private static List<Guid> ReadTrackIds(JsonElement data) =>
        data.GetProperty("trackIds").EnumerateArray()
            .Select(e => Guid.Parse(e.GetString()!))
            .ToList();

    private async Task<List<AlbumTrack>> GetAlbumTrackRowsAsync(Guid albumId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return await db.AlbumTracks.AsNoTracking()
            .Where(at => at.AlbumId == albumId)
            .OrderBy(at => at.Position)
            .ToListAsync();
    }

    private static MultipartFormDataContent CreatePngUploadForm()
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(TinyPngBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", "cover.png");
        return form;
    }
}
