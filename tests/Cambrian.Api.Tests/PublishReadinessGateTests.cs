using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// End-to-end suite for the publish readiness gate: PUT /creator/tracks/{id}
/// with visibility=public runs synchronous media validation (promote-on-publish),
/// promoting Uploaded media to Ready on success and refusing with a
/// machine-readable 409 { success, error, code } when the media is invalid or
/// not promotable. Also covers the upload side (fresh uploads land hidden with
/// an Uploaded TrackMedia row) and the admin restore fallback (a restore only
/// returns a track to public when its media is Ready).
/// </summary>
public sealed class PublishReadinessGateTests : IClassFixture<PublishReadinessGateTests.PublishGateFixture>
{
    private const string Password = "Test1234!@";

    private readonly PublishGateFixture _fixture;

    public PublishReadinessGateTests(PublishGateFixture fixture) => _fixture = fixture;

    // ───────────────────────── shared setup ─────────────────────────

    private sealed record PublishContext(HttpClient Creator, string CreatorUserId, Guid TrackId);

    /// <summary>Creator (role + tier + username) with one hidden track whose TrackMedia row is pre-seeded in the given state.</summary>
    private async Task<PublishContext> CreateHiddenTrackAsync(string mediaState, string? failureCode = null)
    {
        var seed = Guid.NewGuid().ToString("N");
        var email = $"pubgate-{seed}@cambrian.com";
        var creator = await _fixture.CreateRoleClientAsync(email, Password, "Creator", $"pubgate{seed[..8]}");
        await _fixture.SetCreatorTierAsync(email, Cambrian.Domain.Enums.CreatorTier.Creator);
        var creatorUserId = await _fixture.GetUserIdAsync(email);
        var trackId = await _fixture.SeedTrackAsync(creatorUserId, "Publish Gate Beat", visibility: "hidden");
        await _fixture.SeedMediaAsync(trackId, mediaState, failureCode);
        return new PublishContext(creator, creatorUserId, trackId);
    }

    private async Task<Track> ReadTrackAsync(Guid trackId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
    }

    private async Task<TrackMedia> ReadMediaAsync(Guid trackId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return await db.TrackMedia.AsNoTracking().SingleAsync(x => x.TrackId == trackId);
    }

    // ───────────────────────── 1. publish-time promotion ─────────────────────────

    [Fact]
    public async Task PublishingUploadedMediaValidatesPromotesToReadyAndGoesPublic()
    {
        var ctx = await CreateHiddenTrackAsync(TrackMediaStates.Uploaded);

        var res = await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new
        {
            visibility = "public",
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(ctx.TrackId.ToString(), body.GetProperty("data").GetProperty("id").GetString());

        Assert.Equal("public", (await ReadTrackAsync(ctx.TrackId)).Visibility);
        var media = await ReadMediaAsync(ctx.TrackId);
        Assert.Equal(TrackMediaStates.Ready, media.State);
        Assert.NotNull(media.ValidatedAtUtc);
        Assert.Null(media.FailureCode);
        Assert.Equal(1, _fixture.Validation.CallCount(ctx.TrackId));

        // The public catalog URL is live immediately after the promotion.
        var anon = _fixture.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/tracks/{ctx.TrackId}")).StatusCode);
    }

    // ───────────────────────── 2. invalid media fails closed ─────────────────────────

    [Fact]
    public async Task PublishingInvalidMediaReturns409WithCodeQuarantinesAndStaysHidden()
    {
        var ctx = await CreateHiddenTrackAsync(TrackMediaStates.Uploaded);
        _fixture.Validation.SetFailure(ctx.TrackId, "decode_probe_failed");

        var res = await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new
        {
            visibility = "public",
        });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("decode_probe_failed", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));

        Assert.Equal("hidden", (await ReadTrackAsync(ctx.TrackId)).Visibility);
        var media = await ReadMediaAsync(ctx.TrackId);
        Assert.Equal(TrackMediaStates.Quarantined, media.State);
        Assert.Equal("decode_probe_failed", media.FailureCode);

        // Still invisible to the public after the refused publish.
        var anon = _fixture.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/tracks/{ctx.TrackId}")).StatusCode);
    }

    // ───────────────────────── 3. quarantined media is never revalidated ─────────────────────────

    [Fact]
    public async Task PublishingQuarantinedMediaIsRefusedWithoutConsultingValidation()
    {
        var ctx = await CreateHiddenTrackAsync(TrackMediaStates.Quarantined, failureCode: "media_parse_failed");
        var before = await ReadMediaAsync(ctx.TrackId);

        var res = await ctx.Creator.PutAsJsonAsync($"/creator/tracks/{ctx.TrackId}", new
        {
            visibility = "public",
        });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("track_not_ready", body.GetProperty("code").GetString());

        Assert.Equal(0, _fixture.Validation.CallCount(ctx.TrackId));
        Assert.Equal("hidden", (await ReadTrackAsync(ctx.TrackId)).Visibility);
        var after = await ReadMediaAsync(ctx.TrackId);
        Assert.Equal(TrackMediaStates.Quarantined, after.State);
        Assert.Equal("media_parse_failed", after.FailureCode);
        Assert.Equal(before.ConcurrencyToken, after.ConcurrencyToken);
    }

    // ───────────────────────── 4. fresh uploads land hidden + Uploaded ─────────────────────────

    [Fact]
    public async Task FreshNonDraftUploadLandsHiddenWithUploadedMediaAndIsInvisibleToAnonymous()
    {
        var seed = Guid.NewGuid().ToString("N");
        var email = $"pubgate-upload-{seed}@cambrian.com";
        var creator = await _fixture.CreateRoleClientAsync(email, Password, "Creator", $"pubup{seed[..8]}");
        await _fixture.SetCreatorTierAsync(email, Cambrian.Domain.Enums.CreatorTier.Creator);

        // Real POST /upload without SaveAsDraft (mp3 magic bytes: FF FB).
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Publish Gate Upload"), "Title");
        var audio = new ByteArrayContent(new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(audio, "Audio", "publish-gate.mp3");

        var upload = await creator.PostAsync("/upload", form);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var uploaded = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var trackId = Guid.Parse(uploaded.GetProperty("trackId").GetString()!);

        // Non-draft uploads still land hidden: publishing is a separate, gated
        // step that must run validation first.
        Assert.Equal("hidden", (await ReadTrackAsync(trackId)).Visibility);
        var media = await ReadMediaAsync(trackId);
        Assert.Equal(TrackMediaStates.Uploaded, media.State);
        Assert.False(string.IsNullOrWhiteSpace(media.ObjectKey));
        Assert.Null(media.ValidatedAtUtc);
        Assert.Null(media.FailureCode);

        // The catalog single-track route conceals the hidden upload.
        var anon = _fixture.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/tracks/{trackId}")).StatusCode);
    }

    // ───────────────────────── 5. admin restore fallback ─────────────────────────

    [Theory]
    [InlineData(TrackMediaStates.Uploaded, "hidden")]
    [InlineData(TrackMediaStates.Ready, "public")]
    public async Task AdminRestoreOnlyReturnsATrackToPublicWhenItsMediaIsReady(
        string mediaState, string expectedVisibility)
    {
        var seed = Guid.NewGuid().ToString("N");
        var email = $"pubgate-restore-{seed}@cambrian.com";
        await _fixture.RegisterUserAsync(email);
        var creatorUserId = await _fixture.GetUserIdAsync(email);
        var trackId = await _fixture.SeedTrackAsync(creatorUserId, "Restore Gate Beat", visibility: "hidden");
        await _fixture.SeedMediaAsync(trackId, mediaState);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        // Put the track into the admin-removed state with a public pre-delete wish.
        var removed = await db.Tracks.SingleAsync(t => t.Id == trackId);
        removed.PreDeleteVisibility = "public";
        removed.PreDeleteStatus = "available";
        removed.Status = "removed";
        removed.DeletedAt = DateTime.UtcNow;
        removed.DeletedByUserId = "admin@test.com";
        await db.SaveChangesAsync();

        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(
            store, null, null, null, null, null, null, null, null);
        var repo = new AdminRepository(
            db,
            users,
            Substitute.For<IEmailService>(),
            Substitute.For<IPaymentGateway>(),
            Substitute.For<IFeatureFlagRepository>());

        var ok = await repo.RestoreTrackAsync(trackId, "admin@test.com");

        Assert.True(ok);
        var restored = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        // PreDeleteVisibility=public is only honoured when a Ready TrackMedia row
        // (with a storage key) exists; otherwise the restore falls back to hidden
        // so an unvalidated object never resurfaces as a live public URL.
        Assert.Equal(expectedVisibility, restored.Visibility);
        Assert.Equal("available", restored.Status);
        Assert.Null(restored.DeletedAt);
        Assert.Null(restored.DeletedByUserId);
        Assert.Null(restored.PreDeleteVisibility);
        Assert.Null(restored.PreDeleteStatus);
    }

    // ───────────────────────── fixture ─────────────────────────

    public sealed class PublishGateFixture : CambrianApiFixture
    {
        public PublishGateValidationStub Validation => Services.GetRequiredService<PublishGateValidationStub>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMediaValidationService>();
                services.AddSingleton<PublishGateValidationStub>();
                services.AddSingleton<IMediaValidationService>(sp => sp.GetRequiredService<PublishGateValidationStub>());
            });
        }

        public async Task SeedMediaAsync(Guid trackId, string state, string? failureCode = null)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            db.TrackMedia.Add(new TrackMedia
            {
                TrackId = trackId,
                ObjectKey = "tracks/test-beat.mp3",
                State = state,
                FailureCode = failureCode,
                StateChangedAtUtc = DateTime.UtcNow,
                SizeBytes = 4,
                ContentType = "audio/mpeg",
                ValidatedAtUtc = state == TrackMediaStates.Ready ? DateTime.UtcNow : null,
                ChecksumSha256 = state == TrackMediaStates.Ready ? new string('a', 64) : null,
                DurationMilliseconds = state == TrackMediaStates.Ready ? 30_000 : null,
                ValidationVersion = state == TrackMediaStates.Ready ? "media-v1" : null,
                ConcurrencyToken = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Deterministic validation stub: valid by default, per-track failures via
    /// <see cref="SetFailure"/>, and per-track call counting so tests can prove
    /// the gate never consulted validation at all.
    /// </summary>
    public sealed class PublishGateValidationStub : IMediaValidationService
    {
        private readonly ConcurrentDictionary<Guid, MediaValidationResult> _results = new();
        private readonly ConcurrentDictionary<Guid, int> _calls = new();

        public void SetFailure(Guid trackId, string code, bool dependencyUnavailable = false) =>
            _results[trackId] = MediaValidationResult.Failure(
                code, "Safe deterministic validation failure.", "test-v1", dependencyUnavailable);

        public int CallCount(Guid trackId) => _calls.GetValueOrDefault(trackId);

        public Task<MediaValidationResult> ValidateAsync(MediaValidationRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _calls.AddOrUpdate(request.TrackId, 1, (_, count) => count + 1);
            if (_results.TryGetValue(request.TrackId, out var result))
                return Task.FromResult(result);
            return Task.FromResult(new MediaValidationResult(
                true, null, null, false, 4, "audio/mpeg", new string('a', 64), 30_000, "test-v1"));
        }
    }
}
