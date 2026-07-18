using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// EDIT-SAFETY regression suite — DO NOT DELETE.
///
/// Editing a track must NEVER create a new track, reset plays, remove likes,
/// break public URLs, or remove analytics. Every fact here seeds a track with
/// real engagement (StreamSessions plays, a TrackBoost like, a completed
/// Purchase), performs ONE mutation (metadata edit, lyrics, behind-the-track,
/// album membership, visibility round-trip, draft publish) and then asserts
/// the full preservation invariant set: same Track row count, same
/// id/CambrianTrackId/CreatedAt/AudioUrl, same engagement counts, and
/// GET /tracks/{id} still resolving to the SAME track.
/// </summary>
[Trait("Category", "Critical")]
public sealed class TrackEditPreservationTests : IClassFixture<CambrianApiFixture>
{
    private const string Password = "Test1234!@";

    private readonly CambrianApiFixture _fixture;

    public TrackEditPreservationTests(CambrianApiFixture fixture) => _fixture = fixture;

    // ───────────────────────── shared setup ─────────────────────────

    private sealed record EngagedTrack(
        HttpClient Creator,
        string CreatorEmail,
        string CreatorUserId,
        Guid TrackId);

    private sealed record EngagementSnapshot(
        int TotalTracks,
        int CreatorTracks,
        string? CambrianTrackId,
        DateTime CreatedAt,
        string? AudioUrl,
        int Plays,
        int Boosts,
        int Purchases);

    /// <summary>
    /// Creator (tier + username + verified email) with one seeded track that has
    /// real engagement: 2 plays (1 anonymous + 1 authenticated — anonymous plays
    /// are deduped to one per (track, IP) per hour, so the second play comes from
    /// a logged-in fan), 1 boost from a verified fan (self-boosts are rejected),
    /// and (by default) 1 completed purchase. Pass withPurchase: false for the
    /// visibility round-trip test — purchased tracks refuse unpublishing.
    /// </summary>
    private async Task<EngagedTrack> CreateEngagedTrackAsync(bool withPurchase = true)
    {
        var seed = Guid.NewGuid().ToString("N");
        var creatorEmail = $"editsafe-{seed}@cambrian.com";
        var creator = await _fixture.CreateRoleClientAsync(
            creatorEmail, Password, "Creator", $"editsafe{seed[..8]}");
        await _fixture.SetCreatorTierAsync(creatorEmail, Cambrian.Domain.Enums.CreatorTier.Creator);
        var creatorUserId = await _fixture.GetUserIdAsync(creatorEmail);

        var trackId = await _fixture.SeedTrackAsync(creatorUserId, "Edit Safety Beat");
        await MarkMediaReadyAsync(trackId);

        // Play 1: anonymous (must report "started", not "already_counted").
        var anon = _fixture.CreateClient();
        var anonStart = await anon.PostAsJsonAsync("/stream/start", new { trackId = trackId.ToString() });
        anonStart.StatusCode.Should().Be(HttpStatusCode.OK);
        var anonData = (await anonStart.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        anonData.GetProperty("status").GetString().Should().Be("started");

        // Play 2 + like: a verified fan (different user — self-boosts are rejected).
        var fanEmail = $"editsafe-fan-{seed}@cambrian.com";
        var fan = await _fixture.CreateAuthenticatedClientAsync(fanEmail, Password);
        var fanStart = await fan.PostAsJsonAsync("/stream/start", new { trackId = trackId.ToString() });
        fanStart.StatusCode.Should().Be(HttpStatusCode.OK);

        var boost = await fan.PostAsync($"/tracks/{trackId}/boost", null);
        boost.IsSuccessStatusCode.Should().BeTrue(
            "a verified fan must be able to boost a public track (got {0})", boost.StatusCode);

        // Purchase: seeded directly, like the entitlement suites do.
        if (withPurchase)
        {
            var fanUserId = await _fixture.GetUserIdAsync(fanEmail);
            await _fixture.SeedCompletedPurchaseAsync(fanUserId, trackId);
        }

        // Fail fast if the engagement seeding semantics ever drift.
        var baseline = await CaptureAsync(trackId, creatorUserId);
        baseline.Plays.Should().Be(2, "one anonymous and one authenticated play were recorded");
        baseline.Boosts.Should().Be(1);
        baseline.Purchases.Should().Be(withPurchase ? 1 : 0);

        return new EngagedTrack(creator, creatorEmail, creatorUserId, trackId);
    }

    private async Task<EngagementSnapshot> CaptureAsync(Guid trackId, string creatorUserId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);

        return new EngagementSnapshot(
            TotalTracks: await db.Tracks.CountAsync(),
            CreatorTracks: await db.Tracks.CountAsync(t => t.CreatorId == creatorUserId),
            CambrianTrackId: track.CambrianTrackId,
            CreatedAt: track.CreatedAt,
            AudioUrl: track.AudioUrl,
            Plays: await db.StreamSessions.CountAsync(s => s.TrackId == trackId),
            Boosts: await db.TrackBoosts.CountAsync(b => b.TrackId == trackId),
            Purchases: await db.Purchases.CountAsync(p => p.TrackId == trackId));
    }

    /// <summary>The full invariant set every edit must satisfy.</summary>
    private async Task AssertTrackPreservedAsync(EngagedTrack ctx, EngagementSnapshot before)
    {
        var after = await CaptureAsync(ctx.TrackId, ctx.CreatorUserId);

        after.TotalTracks.Should().Be(before.TotalTracks, "an edit must never create or delete Track rows");
        after.CreatorTracks.Should().Be(before.CreatorTracks, "an edit must never add or remove the creator's tracks");
        after.CambrianTrackId.Should().Be(before.CambrianTrackId, "the public Cambrian track id must survive edits");
        after.CreatedAt.Should().Be(before.CreatedAt, "a changed CreatedAt would mean the row was recreated");
        after.AudioUrl.Should().Be(before.AudioUrl, "the stored audio key must survive edits");
        after.Plays.Should().Be(before.Plays, "plays (StreamSessions) must survive edits");
        after.Boosts.Should().Be(before.Boosts, "likes (TrackBoosts) must survive edits");
        after.Purchases.Should().Be(before.Purchases, "purchase analytics must survive edits");

        // The public track URL keeps working and resolves to the SAME track.
        var anon = _fixture.CreateClient();
        var res = await anon.GetAsync($"/tracks/{ctx.TrackId}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, "editing must never break the public track URL");
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("id").GetString().Should().Be(ctx.TrackId.ToString());
    }

    private static string[] TrackIdsOf(JsonElement collection) =>
        collection.GetProperty("trackIds").EnumerateArray()
            .Select(e => e.GetString()!.ToLowerInvariant())
        .ToArray();

    private async Task MarkMediaReadyAsync(Guid trackId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var stateMachine = scope.ServiceProvider.GetRequiredService<IMediaStateMachine>();
        var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        var media = await stateMachine.InitializeLegacyAsync(trackId, track.AudioUrl);
        if (media.State == TrackMediaStates.Ready)
            return;

        var validating = await stateMachine.TransitionAsync(
            trackId, media.ConcurrencyToken, TrackMediaStates.Validating, new MediaStateMetadata());
        await stateMachine.TransitionAsync(
            trackId,
            validating.ConcurrencyToken,
            TrackMediaStates.Ready,
            new MediaStateMetadata(
                ValidatedAtUtc: DateTime.UtcNow,
                SizeBytes: 4,
                ContentType: "audio/mpeg",
                ChecksumSha256: new string('a', 64),
                DurationMilliseconds: 1_000,
                ValidationVersion: "test-v1"));
    }

    // ───────────────────────── 1. metadata edit ─────────────────────────

    [Fact]
    public async Task FullMetadataEdit_PreservesIdentityUrlsAndEngagement()
    {
        var ctx = await CreateEngagedTrackAsync();
        var before = await CaptureAsync(ctx.TrackId, ctx.CreatorUserId);

        var res = await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new
        {
            title = "Renamed After Launch",
            description = "Complete metadata rewrite after release.",
            primaryGenre = "Synthwave",
            mood = "Dark",
            tags = "retro, 80s, analog",
            nonExclusivePriceCents = 1499,
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("id").GetString().Should().Be(ctx.TrackId.ToString(),
            "an edit must return the SAME track, never a new one");
        data.GetProperty("title").GetString().Should().Be("Renamed After Launch");

        await AssertTrackPreservedAsync(ctx, before);
    }

    // ───────────────────────── 2. lyrics upsert + clear ─────────────────────────

    [Fact]
    public async Task LyricsUpsertThenClear_NeverTouchesTheTrackRowOrEngagement()
    {
        var ctx = await CreateEngagedTrackAsync();
        var before = await CaptureAsync(ctx.TrackId, ctx.CreatorUserId);

        var upsert = await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}/lyrics", new
        {
            lyrics = "Verse one\nChorus\nVerse two",
            language = "en",
        });
        upsert.StatusCode.Should().Be(HttpStatusCode.OK);
        var lyricsDto = (await upsert.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        lyricsDto.GetProperty("trackId").GetString().Should().Be(ctx.TrackId.ToString());
        lyricsDto.GetProperty("language").GetString().Should().Be("en");

        // Explicit, version-checked deletion removes only the companion row.
        var lyricsVersion = lyricsDto.GetProperty("version").GetInt32();
        var clear = await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}/lyrics", new
        {
            deleteLyrics = true,
            version = lyricsVersion,
        });
        clear.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            (await db.TrackLyrics.CountAsync(l => l.TrackId == ctx.TrackId))
                .Should().Be(0, "clearing lyrics removes only the companion row");
        }

        await AssertTrackPreservedAsync(ctx, before);
    }

    // ───────────────────────── 3. behind-the-track upsert ─────────────────────────

    [Fact]
    public async Task BehindTheTrackUpsert_NeverTouchesTheTrackRowOrEngagement()
    {
        var ctx = await CreateEngagedTrackAsync();
        var before = await CaptureAsync(ctx.TrackId, ctx.CreatorUserId);

        var res = await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}/behind-the-track", new
        {
            story = "Made in one night after the show.",
            youtubeUrl = "https://youtu.be/dQw4w9WgXcQ",
            toolsUsed = new[] { "Suno v5", "Ableton Live" },
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        dto.GetProperty("trackId").GetString().Should().Be(ctx.TrackId.ToString());
        dto.GetProperty("toolsUsed").GetArrayLength().Should().Be(2);

        // Publicly readable, still attributed to the same track.
        var anon = _fixture.CreateClient();
        var pub = await anon.GetAsync($"/tracks/{ctx.TrackId}/behind-the-track");
        pub.StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertTrackPreservedAsync(ctx, before);
    }

    // ───────────────────────── 4. album lifecycle ─────────────────────────

    [Fact]
    public async Task AlbumCreateReorderDelete_LeavesTracksAndEngagementUntouched()
    {
        var ctx = await CreateEngagedTrackAsync();
        var trackId2 = await _fixture.SeedTrackAsync(ctx.CreatorUserId, "Edit Safety Beat B");
        var before = await CaptureAsync(ctx.TrackId, ctx.CreatorUserId);

        // Create an album containing both tracks (trackIds CSV order = track order).
        var create = await ctx.Creator.PostAsJsonAsync("/creator-profile/me/collections", new
        {
            title = $"Edit Safety EP {Guid.NewGuid():N}"[..30],
            description = "Album membership is a relationship, never a copy.",
            trackIds = $"{ctx.TrackId},{trackId2}",
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var albumId = Guid.Parse(created.GetProperty("id").GetString()!);
        TrackIdsOf(created).Should().Equal(ctx.TrackId.ToString(), trackId2.ToString());

        // Reorder: CSV order becomes the new positions.
        var reorder = await ctx.Creator.PutAsJsonAsync($"/creator-profile/me/collections/{albumId}", new
        {
            trackIds = $"{trackId2},{ctx.TrackId}",
        });
        reorder.StatusCode.Should().Be(HttpStatusCode.OK);
        var reordered = (await reorder.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        TrackIdsOf(reordered).Should().Equal(trackId2.ToString(), ctx.TrackId.ToString());

        // Delete the album: join rows go, Track rows NEVER do.
        var del = await ctx.Creator.DeleteAsync($"/creator-profile/me/collections/{albumId}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            (await db.AlbumTracks.CountAsync(at => at.AlbumId == albumId))
                .Should().Be(0, "deleting an album removes its AlbumTracks join rows");
            (await db.Tracks.CountAsync(t => t.Id == ctx.TrackId || t.Id == trackId2))
                .Should().Be(2, "deleting an album must NEVER delete Track rows");
        }

        await AssertTrackPreservedAsync(ctx, before);
    }

    // ───────────────────────── 5. visibility round-trip ─────────────────────────

    [Fact]
    public async Task VisibilityHiddenThenPublic_PreservesIdEngagementAndPublicUrl()
    {
        // No purchase: purchased tracks refuse unpublishing (next test).
        var ctx = await CreateEngagedTrackAsync(withPurchase: false);
        var before = await CaptureAsync(ctx.TrackId, ctx.CreatorUserId);
        var anon = _fixture.CreateClient();

        // Unpublish.
        var hide = await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new
        {
            visibility = "hidden",
        });
        hide.StatusCode.Should().Be(HttpStatusCode.OK);

        // Hidden: gone for anonymous visitors, still visible to the owner.
        (await anon.GetAsync($"/tracks/{ctx.TrackId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "hidden tracks must 404 for anonymous requesters");
        (await ctx.Creator.GetAsync($"/tracks/{ctx.TrackId}")).StatusCode
            .Should().Be(HttpStatusCode.OK, "the owner must still see their hidden track");

        // Republish.
        var publish = await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new
        {
            visibility = "public",
        });
        publish.StatusCode.Should().Be(HttpStatusCode.OK);
        var republished = (await publish.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        republished.GetProperty("id").GetString().Should().Be(ctx.TrackId.ToString(),
            "the visibility round-trip must keep the SAME track id");

        // Full invariant set — including the anonymous GET /tracks/{id} → 200.
        await AssertTrackPreservedAsync(ctx, before);
    }

    [Fact]
    public async Task UnpublishingAPurchasedTrack_IsRefused_AndNothingChanges()
    {
        // Fans who paid for a track must never lose streaming access because
        // the creator unpublished it — the API refuses with a clear error.
        var ctx = await CreateEngagedTrackAsync(); // includes 1 completed purchase
        var before = await CaptureAsync(ctx.TrackId, ctx.CreatorUserId);

        var hide = await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new
        {
            visibility = "hidden",
        });
        hide.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "unpublishing a purchased track would revoke buyers' streaming access");
        var body = await hide.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("purchased");

        // The track stays public and untouched — full invariant set.
        await AssertTrackPreservedAsync(ctx, before);
    }

    // ───────────────────────── 6. draft upload + publish ─────────────────────────

    [Fact]
    public async Task DraftUpload_HiddenFromAnon_VisibleToOwner_PublishKeepsSameTrackId()
    {
        var ctx = await CreateEngagedTrackAsync();
        var anon = _fixture.CreateClient();

        // Upload with SaveAsDraft=true (mp3 magic bytes: FF FB).
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Draft Safety Track"), "Title");
        form.Add(new StringContent("true"), "SaveAsDraft");
        var audio = new ByteArrayContent(new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(audio, "Audio", "draft-safety.mp3");

        var upload = await ctx.Creator.PostAsync("/upload", form);
        upload.StatusCode.Should().Be(HttpStatusCode.Created);
        var uploaded = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var draftId = Guid.Parse(uploaded.GetProperty("trackId").GetString()!);
        await MarkMediaReadyAsync(draftId);

        // Snapshot the draft's identity straight from the DB.
        string? cambrianTrackId;
        DateTime createdAt;
        string? audioUrl;
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var draft = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == draftId);
            draft.Visibility.Should().Be("hidden", "SaveAsDraft=true must create the track hidden");
            cambrianTrackId = draft.CambrianTrackId;
            createdAt = draft.CreatedAt;
            audioUrl = draft.AudioUrl;
        }

        // Anonymous visitors cannot see the draft; the owner sees it in their list.
        (await anon.GetAsync($"/tracks/{draftId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "drafts must be invisible to anonymous requesters");

        var mine = await ctx.Creator.GetAsync("/creator/tracks");
        mine.StatusCode.Should().Be(HttpStatusCode.OK);
        var mineItems = (await mine.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        mineItems.EnumerateArray()
            .Select(t => t.GetProperty("id").GetString()!.ToLowerInvariant())
            .Should().Contain(draftId.ToString(), "the owner must see their draft in GET /creator/tracks");

        // Publishing is an in-place visibility flip — SAME track id, same row.
        var publish = await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{draftId}", new
        {
            visibility = "public",
        });
        publish.StatusCode.Should().Be(HttpStatusCode.OK);
        var published = (await publish.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        published.GetProperty("id").GetString().Should().Be(draftId.ToString(),
            "publishing a draft must keep the SAME track id");

        var publicGet = await anon.GetAsync($"/tracks/{draftId}");
        publicGet.StatusCode.Should().Be(HttpStatusCode.OK, "the draft's URL becomes public after publishing");
        var publicData = (await publicGet.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        publicData.GetProperty("id").GetString().Should().Be(draftId.ToString());

        // Identity fields survived the publish untouched.
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            (await db.Tracks.CountAsync(t => t.Id == draftId)).Should().Be(1);
            var row = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == draftId);
            row.Visibility.Should().Be("public");
            row.CambrianTrackId.Should().Be(cambrianTrackId, "publishing must not regenerate the Cambrian track id");
            row.CreatedAt.Should().Be(createdAt, "publishing must not recreate the row");
            row.AudioUrl.Should().Be(audioUrl, "publishing must not touch the stored audio");
        }
    }
}
