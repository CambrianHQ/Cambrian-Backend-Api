using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cambrian.Api.Tests;

public sealed class PlaybackV1IntegrationTests : IClassFixture<PlaybackV1IntegrationTests.PlaybackFixture>
{
    private readonly PlaybackFixture _fixture;

    public PlaybackV1IntegrationTests(PlaybackFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AnonymousPublicPlaybackReturnsFreshTicketAndRequiredHeaders()
    {
        var ownerEmail = $"playback-owner-{Guid.NewGuid():N}@cambrian.test";
        await _fixture.RegisterUserAsync(ownerEmail);
        var ownerId = await _fixture.GetUserIdAsync(ownerEmail);
        var trackId = await _fixture.SeedTrackAsync(ownerId, visibility: "public");
        await _fixture.SeedMediaAsync(trackId, TrackMediaStates.Ready);

        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync($"/api/v1/tracks/{trackId:D}/playback");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.True(response.Headers.Contains("X-Request-ID"));
        Assert.Equal("test-release", response.Headers.GetValues("X-Backend-Release").Single());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.Equal("Ready", data.GetProperty("mediaState").GetString());
        var location = data.GetProperty("location").GetString()!;
        Assert.Contains($"/stream/{trackId:D}/audio?ticket=", location);
        Assert.DoesNotContain("tracks/test-beat.mp3", location);

        var locationUri = new Uri(location);
        using var media = await client.GetAsync(locationUri.PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, media.StatusCode);
        Assert.Contains("no-store", media.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task PrivateTrackIsConcealedFromAnonymousAndUnrelatedAndGrantedToOwner()
    {
        var ownerEmail = $"private-owner-{Guid.NewGuid():N}@cambrian.test";
        var ownerClient = await _fixture.CreateAuthenticatedClientAsync(ownerEmail);
        var ownerId = await _fixture.GetUserIdAsync(ownerEmail);
        var trackId = await _fixture.SeedTrackAsync(ownerId, visibility: "hidden");
        await _fixture.SeedMediaAsync(trackId, TrackMediaStates.Ready);

        using var anonymous = _fixture.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound,
            (await anonymous.GetAsync($"/api/v1/tracks/{trackId:D}/playback")).StatusCode);

        // Authenticated non-owners get the same 404 mask as anonymous callers so
        // hidden track IDs cannot be enumerated via a 403/404 differential.
        using var unrelated = await _fixture.CreateAuthenticatedClientAsync(
            $"unrelated-{Guid.NewGuid():N}@cambrian.test");
        Assert.Equal(HttpStatusCode.NotFound,
            (await unrelated.GetAsync($"/api/v1/tracks/{trackId:D}/playback")).StatusCode);

        using (ownerClient)
        {
            Assert.Equal(HttpStatusCode.OK,
                (await ownerClient.GetAsync($"/api/v1/tracks/{trackId:D}/playback")).StatusCode);
        }

        using var admin = await _fixture.CreateRoleClientAsync(
            $"admin-{Guid.NewGuid():N}@cambrian.test", "Test1234!@", "Admin");
        Assert.Equal(HttpStatusCode.OK,
            (await admin.GetAsync($"/api/v1/tracks/{trackId:D}/playback")).StatusCode);
    }

    [Theory]
    [InlineData(TrackMediaStates.Draft)]
    [InlineData(TrackMediaStates.Uploading)]
    [InlineData(TrackMediaStates.Uploaded)]
    [InlineData(TrackMediaStates.Processing)]
    [InlineData(TrackMediaStates.Failed)]
    [InlineData(TrackMediaStates.Quarantined)]
    [InlineData(TrackMediaStates.Deleted)]
    public async Task EveryNonReadyMediaStateFailsClosed(string state)
    {
        var email = $"state-{state}-{Guid.NewGuid():N}@cambrian.test";
        await _fixture.RegisterUserAsync(email);
        var id = await _fixture.SeedTrackAsync(await _fixture.GetUserIdAsync(email), visibility: "public");
        await _fixture.SeedMediaAsync(id, state);

        using var response = await _fixture.CreateClient().GetAsync($"/api/v1/tracks/{id:D}/playback");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("track_not_ready", (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task StaleReadyMediaKeepsServingWhenValidationDependencyIsUnavailable()
    {
        var email = $"validation-dep-{Guid.NewGuid():N}@cambrian.test";
        await _fixture.RegisterUserAsync(email);
        var id = await _fixture.SeedTrackAsync(await _fixture.GetUserIdAsync(email), visibility: "public");
        await _fixture.SeedMediaAsync(id, TrackMediaStates.Ready, DateTime.UtcNow.AddDays(-2));
        _fixture.Validation.SetFailure(id, "storage_unavailable", dependencyUnavailable: true);

        // Validation tooling being down is not evidence against previously validated
        // media — playback keeps serving last-known-good and the row stays Ready.
        using var response = await _fixture.CreateClient().GetAsync($"/api/v1/tracks/{id:D}/playback");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        Assert.Equal(TrackMediaStates.Ready,
            (await db.TrackMedia.AsNoTracking().SingleAsync(x => x.TrackId == id)).State);
    }

    [Theory]
    [InlineData("media_object_missing", false, HttpStatusCode.ServiceUnavailable, "media_object_missing", TrackMediaStates.Failed)]
    [InlineData("checksum_mismatch", false, HttpStatusCode.UnprocessableEntity, "media_validation_failed", TrackMediaStates.Quarantined)]
    public async Task StaleReadyMediaFailsSafelyAndTransitionsAsAllowed(
        string validationCode,
        bool dependencyUnavailable,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string expectedState)
    {
        var email = $"validation-{Guid.NewGuid():N}@cambrian.test";
        await _fixture.RegisterUserAsync(email);
        var id = await _fixture.SeedTrackAsync(await _fixture.GetUserIdAsync(email), visibility: "public");
        await _fixture.SeedMediaAsync(id, TrackMediaStates.Ready, DateTime.UtcNow.AddDays(-2));
        _fixture.Validation.SetFailure(id, validationCode, dependencyUnavailable);

        using var response = await _fixture.CreateClient().GetAsync($"/api/v1/tracks/{id:D}/playback");

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString());
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        Assert.Equal(expectedState, (await db.TrackMedia.AsNoTracking().SingleAsync(x => x.TrackId == id)).State);
    }

    [Fact]
    public async Task MissingAndNotReadyTracksReturnStableStructuredErrors()
    {
        using var client = _fixture.CreateClient();
        using var missing = await client.GetAsync($"/api/v1/tracks/{Guid.NewGuid():D}/playback");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("track_not_found", (await missing.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString());

        var email = $"not-ready-{Guid.NewGuid():N}@cambrian.test";
        await _fixture.RegisterUserAsync(email);
        var id = await _fixture.SeedTrackAsync(await _fixture.GetUserIdAsync(email), visibility: "public");
        await _fixture.SeedMediaAsync(id, TrackMediaStates.Uploaded);
        using var notReady = await client.GetAsync($"/api/v1/tracks/{id:D}/playback");
        Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
        Assert.Equal("track_not_ready", (await notReady.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ValidatingWithoutPriorValidationFailsClosed()
    {
        var email = $"validating-fresh-{Guid.NewGuid():N}@cambrian.test";
        await _fixture.RegisterUserAsync(email);
        var id = await _fixture.SeedTrackAsync(await _fixture.GetUserIdAsync(email), visibility: "public");
        await _fixture.SeedMediaAsync(id, TrackMediaStates.Validating);

        using var response = await _fixture.CreateClient().GetAsync($"/api/v1/tracks/{id:D}/playback");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("track_not_ready", (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ValidatingWithPriorValidationKeepsServingDuringRecheck()
    {
        var email = $"validating-prior-{Guid.NewGuid():N}@cambrian.test";
        await _fixture.RegisterUserAsync(email);
        var id = await _fixture.SeedTrackAsync(await _fixture.GetUserIdAsync(email), visibility: "public");
        // SeedMediaAsync only stamps ValidatedAtUtc on Ready rows, so insert the
        // mid-recheck shape directly: parked in Validating with a prior validation.
        using (var seedScope = _fixture.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            seedDb.TrackMedia.Add(new TrackMedia
            {
                TrackId = id,
                ObjectKey = "tracks/test-beat.mp3",
                State = TrackMediaStates.Validating,
                StateChangedAtUtc = DateTime.UtcNow,
                ValidatedAtUtc = DateTime.UtcNow.AddHours(-1),
                SizeBytes = 4,
                ContentType = "audio/mpeg",
                ChecksumSha256 = new string('a', 64),
                DurationMilliseconds = 30_000,
                ValidationVersion = "media-v1",
                ConcurrencyToken = Guid.NewGuid(),
            });
            await seedDb.SaveChangesAsync();
        }

        using var response = await _fixture.CreateClient().GetAsync($"/api/v1/tracks/{id:D}/playback");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(TrackMediaStates.Validating, data.GetProperty("mediaState").GetString());
        Assert.Contains($"/stream/{id:D}/audio?ticket=", data.GetProperty("location").GetString());
    }

    [Fact]
    public async Task TrackWithoutMediaRowFailsClosedAsNotReady()
    {
        var email = $"no-media-{Guid.NewGuid():N}@cambrian.test";
        await _fixture.RegisterUserAsync(email);
        var id = await _fixture.SeedTrackAsync(await _fixture.GetUserIdAsync(email), visibility: "public");

        using var response = await _fixture.CreateClient().GetAsync($"/api/v1/tracks/{id:D}/playback");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("track_not_ready", (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CambrianTrackIdResolvesToTheSameGuidTrackAndUnknownIdIsNotFound()
    {
        var email = $"camb-id-{Guid.NewGuid():N}@cambrian.test";
        await _fixture.RegisterUserAsync(email);
        var id = await _fixture.SeedTrackAsync(await _fixture.GetUserIdAsync(email), visibility: "public");
        await _fixture.SeedMediaAsync(id, TrackMediaStates.Ready);
        string cambrianTrackId;
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            cambrianTrackId = (await db.Tracks.AsNoTracking().SingleAsync(x => x.Id == id)).CambrianTrackId;
        }

        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync($"/api/v1/tracks/{cambrianTrackId}/playback");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(id, Guid.Parse(data.GetProperty("trackId").GetString()!));

        using var unknown = await client.GetAsync("/api/v1/tracks/CAMB-TRK-ZZZZ0000/playback");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("track_not_found", (await unknown.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ErrorAndSuccessResponsesCarryTracingMetadataAndNoStore()
    {
        using var client = _fixture.CreateClient();

        using var missing = await client.GetAsync($"/api/v1/tracks/{Guid.NewGuid():D}/playback");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("no-store", missing.Headers.CacheControl!.ToString());
        var error = (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("requestId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("traceId").GetString()));

        var email = $"trace-meta-{Guid.NewGuid():N}@cambrian.test";
        await _fixture.RegisterUserAsync(email);
        var id = await _fixture.SeedTrackAsync(await _fixture.GetUserIdAsync(email), visibility: "public");
        await _fixture.SeedMediaAsync(id, TrackMediaStates.Ready);
        using var success = await client.GetAsync($"/api/v1/tracks/{id:D}/playback");
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Contains("no-store", success.Headers.CacheControl!.ToString());
        var data = (await success.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("requestId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task TicketedHeadReturnsMetadataAndNoBody()
    {
        var email = $"head-owner-{Guid.NewGuid():N}@cambrian.test";
        await _fixture.RegisterUserAsync(email);
        var id = await _fixture.SeedTrackAsync(await _fixture.GetUserIdAsync(email), visibility: "public");
        await _fixture.SeedMediaAsync(id, TrackMediaStates.Ready);
        using var client = _fixture.CreateClient();
        var info = await client.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{id:D}/playback");
        var location = new Uri(info.GetProperty("data").GetProperty("location").GetString()!);

        using var request = new HttpRequestMessage(HttpMethod.Head, location.PathAndQuery);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        Assert.Equal(4, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    public sealed class PlaybackFixture : CambrianApiFixture
    {
        public PlaybackValidationStub Validation => Services.GetRequiredService<PlaybackValidationStub>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("PlaybackMedia:TicketSigningKey", "integration-playback-key-at-least-32-bytes");
            builder.UseSetting("PlaybackMedia:ProductionProbeSigningKey", "integration-probe-key-at-least-32-bytes");
            builder.UseSetting("PlaybackMedia:BackendRelease", "test-release");
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMediaValidationService>();
                services.AddSingleton<PlaybackValidationStub>();
                services.AddSingleton<IMediaValidationService>(sp => sp.GetRequiredService<PlaybackValidationStub>());
            });
        }

        public async Task SeedMediaAsync(Guid trackId, string state, DateTime? validatedAtUtc = null)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            db.TrackMedia.Add(new TrackMedia
            {
                TrackId = trackId,
                ObjectKey = "tracks/test-beat.mp3",
                State = state,
                StateChangedAtUtc = DateTime.UtcNow,
                ValidatedAtUtc = state == TrackMediaStates.Ready ? validatedAtUtc ?? DateTime.UtcNow : null,
                SizeBytes = 4,
                ContentType = "audio/mpeg",
                ChecksumSha256 = state == TrackMediaStates.Ready ? new string('a', 64) : null,
                DurationMilliseconds = state == TrackMediaStates.Ready ? 30_000 : null,
                ValidationVersion = state == TrackMediaStates.Ready ? "media-v1" : null,
                ConcurrencyToken = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }
    }

    public sealed class PlaybackValidationStub : IMediaValidationService
    {
        private readonly Dictionary<Guid, MediaValidationResult> _results = new();

        public void SetFailure(Guid trackId, string code, bool dependencyUnavailable) =>
            _results[trackId] = MediaValidationResult.Failure(
                code, "Safe deterministic validation failure.", "test-v1", dependencyUnavailable);

        public Task<MediaValidationResult> ValidateAsync(MediaValidationRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_results.TryGetValue(request.TrackId, out var result))
                return Task.FromResult(result);
            return Task.FromResult(new MediaValidationResult(
                true, null, null, false, 4, "audio/mpeg", new string('a', 64), 30_000, "test-v1"));
        }
    }
}
