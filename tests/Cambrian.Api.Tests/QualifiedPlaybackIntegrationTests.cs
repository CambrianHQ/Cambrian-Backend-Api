using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// Endpoint-to-PostgreSQL proof for the qualified-play authority. These tests use
/// the real API container and always re-read writes through a fresh DbContext.
/// </summary>
public sealed class QualifiedPlaybackIntegrationTests : IClassFixture<QualifiedPlaybackApiFixture>
{
    private readonly QualifiedPlaybackApiFixture _fixture;

    public QualifiedPlaybackIntegrationTests(QualifiedPlaybackApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task StartDoesNotCount_QualifiedStopCommitsLedgerStatsAndPublicProjection()
    {
        _fixture.Clock.SetUtcNow(Utc(2028, 1, 3, 12));
        var (ownerId, _) = await CreateUserAsync("play-owner");
        var (_, listener) = await CreateUserAsync("play-listener");
        var trackId = await SeedTrackAsync(ownerId, "Projection Proof", duration: "2:00");
        await SeedLegacyStatsAsync(trackId, 7);

        var started = await StartAsync(listener, trackId);
        Assert.Equal("started", started.GetProperty("status").GetString());
        Assert.Equal(30, started.GetProperty("qualificationThresholdSeconds").GetDouble());
        var streamId = started.GetProperty("streamId").GetGuid();

        await WithFreshDbAsync(async db =>
        {
            Assert.Empty(await db.QualifiedPlayEvents.Where(x => x.TrackId == trackId).ToListAsync());
            var before = await db.TrackStats.AsNoTracking().SingleAsync(x => x.TrackId == trackId);
            Assert.Equal(7, before.PlayCount);
            Assert.Equal(7, before.LegacyPlayCount);
            Assert.Equal(0, before.QualifiedPlayCount);
        });

        _fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        var stopped = await StopAsync(listener, streamId, activePlaybackSeconds: 30);
        Assert.Equal("qualified", stopped.GetProperty("status").GetString());
        Assert.True(stopped.GetProperty("qualified").GetBoolean());
        Assert.True(stopped.GetProperty("counted").GetBoolean());
        Assert.Equal(8, stopped.GetProperty("lifetimePlayCount").GetInt64());

        await WithFreshDbAsync(async db =>
        {
            var play = await db.QualifiedPlayEvents.AsNoTracking().SingleAsync(x => x.TrackId == trackId);
            var session = await db.StreamSessions.AsNoTracking().SingleAsync(x => x.Id == streamId);
            var stats = await db.TrackStats.AsNoTracking().SingleAsync(x => x.TrackId == trackId);

            Assert.Equal(streamId, play.PlaybackSessionId);
            Assert.Equal(_fixture.Clock.GetUtcNow().UtcDateTime, play.QualifiedAtUtc);
            Assert.NotNull(play.AggregatedAtUtc);
            Assert.Equal("qualified", session.QualificationStatus);
            Assert.Equal(play.QualifiedAtUtc, session.QualifiedAtUtc);
            Assert.Equal(1, stats.QualifiedPlayCount);
            Assert.Equal(7, stats.LegacyPlayCount);
            Assert.Equal(stats.LegacyPlayCount + stats.QualifiedPlayCount, stats.PlayCount);
        });

        var publicResponse = await _fixture.CreateClient().GetAsync($"/api/public/tracks/{trackId:D}");
        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        var publicTrack = await ReadDataAsync(publicResponse);
        Assert.Equal(8, publicTrack.GetProperty("plays").GetInt64());
    }

    [Fact]
    public async Task QualificationThreshold_IsMinimumOfThirtySecondsAndHalfDuration()
    {
        _fixture.Clock.SetUtcNow(Utc(2028, 1, 4, 12));
        var (ownerId, _) = await CreateUserAsync("threshold-owner");
        var (_, listener) = await CreateUserAsync("threshold-listener");
        var shortTrack = await SeedTrackAsync(ownerId, "Twenty Seconds", duration: "0:20");
        var longTrack = await SeedTrackAsync(ownerId, "Two Minutes", duration: "120");

        var shortStart = await StartAsync(listener, shortTrack);
        var longStart = await StartAsync(listener, longTrack);

        Assert.Equal(10, shortStart.GetProperty("qualificationThresholdSeconds").GetDouble());
        Assert.Equal(30, longStart.GetProperty("qualificationThresholdSeconds").GetDouble());
    }

    [Fact]
    public async Task ActivePauseAndSeekEvidence_AccumulatesOnlyProvenActiveTime()
    {
        _fixture.Clock.SetUtcNow(Utc(2028, 1, 5, 12));
        var (ownerId, _) = await CreateUserAsync("evidence-owner");
        var (_, listener) = await CreateUserAsync("evidence-listener");
        var trackId = await SeedTrackAsync(ownerId, "Seek Proof", duration: "2:00");

        var started = await StartAsync(listener, trackId);
        var streamId = started.GetProperty("streamId").GetGuid();
        _fixture.Clock.Advance(TimeSpan.FromSeconds(40));

        // A near-end seek and a high seek count add no time. Only the explicit 29
        // active seconds are accepted, so this segment remains below threshold.
        var firstStop = await StopAsync(listener, streamId,
            activePlaybackSeconds: 29, seekCount: 20, endingPositionSeconds: 119);
        Assert.Equal("pending", firstStop.GetProperty("status").GetString());
        Assert.Equal(29, firstStop.GetProperty("activePlaybackSeconds").GetDouble());
        Assert.False(firstStop.GetProperty("counted").GetBoolean());

        var resumed = await StartAsync(listener, trackId);
        Assert.Equal("resumed", resumed.GetProperty("status").GetString());
        Assert.Equal(streamId, resumed.GetProperty("streamId").GetGuid());
        _fixture.Clock.Advance(TimeSpan.FromSeconds(10));

        // Pause time caps even an explicit active-time claim: min(10 reported,
        // 10 elapsed - 2 paused) = 8, for a cumulative 37 seconds.
        var secondStop = await StopAsync(listener, streamId,
            activePlaybackSeconds: 10, pausedSeconds: 2);
        Assert.Equal("qualified", secondStop.GetProperty("status").GetString());
        Assert.Equal(37, secondStop.GetProperty("activePlaybackSeconds").GetDouble());

        await WithFreshDbAsync(async db =>
        {
            Assert.Equal(1, await db.QualifiedPlayEvents.CountAsync(x => x.TrackId == trackId));
            Assert.Equal(37, (await db.QualifiedPlayEvents.SingleAsync(x => x.TrackId == trackId)).ActivePlaybackSeconds);
        });
    }

    [Fact]
    public async Task RepeatedStopCannotAccumulateTheSamePlaybackSegment()
    {
        _fixture.Clock.SetUtcNow(Utc(2028, 1, 5, 14));
        var (ownerId, _) = await CreateUserAsync("stop-replay-owner");
        var (_, listener) = await CreateUserAsync("stop-replay-listener");
        var trackId = await SeedTrackAsync(ownerId, "Stop Replay Proof", duration: "1:00");

        var started = await StartAsync(listener, trackId);
        var streamId = started.GetProperty("streamId").GetGuid();
        _fixture.Clock.Advance(TimeSpan.FromSeconds(20));

        var firstStop = await StopAsync(listener, streamId,
            activePlaybackSeconds: 20, idempotencyKey: "segment-one");
        Assert.Equal("pending", firstStop.GetProperty("status").GetString());
        Assert.Equal(20, firstStop.GetProperty("activePlaybackSeconds").GetDouble());

        _fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        var replayedStop = await StopAsync(listener, streamId,
            activePlaybackSeconds: 20, idempotencyKey: "different-retry-key");
        Assert.Equal("pending", replayedStop.GetProperty("status").GetString());
        Assert.True(replayedStop.GetProperty("idempotentReplay").GetBoolean());
        Assert.Equal(20, replayedStop.GetProperty("activePlaybackSeconds").GetDouble());

        await WithFreshDbAsync(async db =>
            Assert.Empty(await db.QualifiedPlayEvents.Where(x => x.TrackId == trackId).ToListAsync()));

        var resumed = await StartAsync(listener, trackId);
        Assert.Equal("resumed", resumed.GetProperty("status").GetString());
        _fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        var qualified = await StopAsync(listener, streamId, activePlaybackSeconds: 10);
        Assert.True(qualified.GetProperty("counted").GetBoolean());
        Assert.Equal(30, qualified.GetProperty("activePlaybackSeconds").GetDouble());
    }

    [Fact]
    public async Task AuthenticatedAndAnonymousIdentities_DedupeIndependentlyWithoutRawIdentifiers()
    {
        _fixture.Clock.SetUtcNow(Utc(2028, 1, 6, 12));
        var (ownerId, _) = await CreateUserAsync("identity-owner");
        var (_, authenticated) = await CreateUserAsync("identity-listener");
        var trackId = await SeedTrackAsync(ownerId, "Identity Proof", duration: "0:20");

        var authStart = await StartAsync(authenticated, trackId);
        _fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await StopAsync(authenticated, authStart.GetProperty("streamId").GetGuid(), activePlaybackSeconds: 10);

        var authReplay = await StartAsync(authenticated, trackId);
        Assert.Equal("already_counted", authReplay.GetProperty("status").GetString());
        Assert.Equal(authStart.GetProperty("streamId").GetGuid(), authReplay.GetProperty("streamId").GetGuid());

        var anonymousA = _fixture.CreateClient();
        var anonAStart = await StartAsync(anonymousA, trackId, "anonymous-device-a");
        Assert.True(anonAStart.GetProperty("anonymousSessionAccepted").GetBoolean());
        _fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await StopAsync(anonymousA, anonAStart.GetProperty("streamId").GetGuid(),
            activePlaybackSeconds: 10, anonymousSessionId: "anonymous-device-a");
        var anonAReplay = await StartAsync(anonymousA, trackId, "anonymous-device-a");
        Assert.Equal("already_counted", anonAReplay.GetProperty("status").GetString());

        var anonymousB = _fixture.CreateClient();
        var anonBStart = await StartAsync(anonymousB, trackId, "anonymous-device-b");
        _fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await StopAsync(anonymousB, anonBStart.GetProperty("streamId").GetGuid(),
            activePlaybackSeconds: 10, anonymousSessionId: "anonymous-device-b");

        await WithFreshDbAsync(async db =>
        {
            var events = await db.QualifiedPlayEvents.AsNoTracking()
                .Where(x => x.TrackId == trackId).ToListAsync();
            Assert.Equal(3, events.Count);
            Assert.Equal(3, events.Select(x => x.ListenerKeyHash).Distinct().Count());
            Assert.Equal(2, events.Count(x => x.ListenerUserId is null));
            Assert.All(events, x => Assert.Equal(64, x.ListenerKeyHash.Length));
            Assert.DoesNotContain(events, x => x.ListenerKeyHash.Contains("anonymous-device", StringComparison.Ordinal));
            Assert.DoesNotContain(events, x => x.AnonymousSessionHash?.Contains("anonymous-device", StringComparison.Ordinal) == true);
        });
    }

    [Fact]
    public async Task OwnerPrivateAndDeletedTracks_CannotCreateQualifiedPlays()
    {
        _fixture.Clock.SetUtcNow(Utc(2028, 1, 7, 12));
        var (ownerId, owner) = await CreateUserAsync("eligibility-owner");
        var (_, listener) = await CreateUserAsync("eligibility-listener");

        var ownerTrack = await SeedTrackAsync(ownerId, "Owner Preview", duration: "0:20");
        var ownerStart = await StartAsync(owner, ownerTrack);
        _fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        var ownerStop = await StopAsync(owner, ownerStart.GetProperty("streamId").GetGuid(), activePlaybackSeconds: 10);
        Assert.Equal("owner_preview", ownerStop.GetProperty("status").GetString());
        Assert.False(ownerStop.GetProperty("qualified").GetBoolean());

        var privateTrack = await SeedTrackAsync(ownerId, "Private", duration: "0:20", visibility: "hidden");
        var privateResponse = await PostStartAsync(listener, privateTrack);
        Assert.Equal(HttpStatusCode.NotFound, privateResponse.StatusCode);
        var privateError = await privateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("track_not_found", privateError.GetProperty("error").GetProperty("code").GetString());

        var deletedTrack = await SeedTrackAsync(ownerId, "Deleted", duration: "0:20",
            status: "removed", deletedAt: _fixture.Clock.GetUtcNow().UtcDateTime);
        var deletedStart = await StartAsync(listener, deletedTrack);
        _fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        var deletedStop = await StopAsync(listener, deletedStart.GetProperty("streamId").GetGuid(), activePlaybackSeconds: 10);
        Assert.Equal("ineligible", deletedStop.GetProperty("status").GetString());

        await WithFreshDbAsync(async db =>
        {
            Assert.Equal(0, await db.QualifiedPlayEvents.CountAsync(x =>
                x.TrackId == ownerTrack || x.TrackId == privateTrack || x.TrackId == deletedTrack));
            Assert.False(await db.TrackStats.AnyAsync(x =>
                x.TrackId == ownerTrack || x.TrackId == privateTrack || x.TrackId == deletedTrack));
        });
    }

    [Fact]
    public async Task AnonymousStop_WithDifferentStableIdentity_IsNotAuthorizedAndCannotPoisonSession()
    {
        _fixture.Clock.SetUtcNow(Utc(2028, 1, 7, 18));
        var (ownerId, _) = await CreateUserAsync("anonymous-owner");
        var trackId = await SeedTrackAsync(ownerId, "Anonymous Ownership", duration: "0:20");
        var anonymous = _fixture.CreateClient();

        var started = await StartAsync(anonymous, trackId, "right-anonymous-identity");
        var streamId = started.GetProperty("streamId").GetGuid();
        _fixture.Clock.Advance(TimeSpan.FromSeconds(10));

        var wrongIdentity = await PostStopAsync(anonymous, streamId,
            activePlaybackSeconds: 10, anonymousSessionId: "wrong-anonymous-identity");
        Assert.Equal(HttpStatusCode.NotFound, wrongIdentity.StatusCode);
        var error = await wrongIdentity.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("playback_session_not_found", error.GetProperty("error").GetProperty("code").GetString());

        await WithFreshDbAsync(async db =>
        {
            var session = await db.StreamSessions.AsNoTracking().SingleAsync(x => x.Id == streamId);
            Assert.Null(session.StoppedAt);
            Assert.Equal(0, session.ActivePlaybackSeconds);
            Assert.Equal("pending", session.QualificationStatus);
            Assert.False(await db.QualifiedPlayEvents.AnyAsync(x => x.PlaybackSessionId == streamId));
        });

        var accepted = await StopAsync(anonymous, streamId,
            activePlaybackSeconds: 10, anonymousSessionId: "right-anonymous-identity");
        Assert.True(accepted.GetProperty("counted").GetBoolean());
    }

    [Fact]
    public async Task ProjectionOverflow_RollsBackSessionLedgerAndStatsAsOneTransaction()
    {
        _fixture.Clock.SetUtcNow(Utc(2028, 1, 7, 20));
        var (ownerId, _) = await CreateUserAsync("rollback-owner");
        var (_, listener) = await CreateUserAsync("rollback-listener");
        var trackId = await SeedTrackAsync(ownerId, "Rollback Proof", duration: "0:20");
        await SeedLegacyStatsAsync(trackId, long.MaxValue);

        var started = await StartAsync(listener, trackId);
        var streamId = started.GetProperty("streamId").GetGuid();
        _fixture.Clock.Advance(TimeSpan.FromSeconds(10));

        // The checked lifetime projection overflows after the service has prepared
        // the session and append-only event in memory, but before SaveChanges. The
        // request must fail without any of those mutations becoming visible.
        var failed = await PostStopAsync(listener, streamId, activePlaybackSeconds: 10);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        await WithFreshDbAsync(async db =>
        {
            Assert.False(await db.QualifiedPlayEvents.AnyAsync(x => x.TrackId == trackId));
            var stats = await db.TrackStats.AsNoTracking().SingleAsync(x => x.TrackId == trackId);
            Assert.Equal(long.MaxValue, stats.PlayCount);
            Assert.Equal(long.MaxValue, stats.LegacyPlayCount);
            Assert.Equal(0, stats.QualifiedPlayCount);

            var session = await db.StreamSessions.AsNoTracking().SingleAsync(x => x.Id == streamId);
            Assert.Null(session.StoppedAt);
            Assert.Null(session.QualifiedAtUtc);
            Assert.Equal(0, session.ActivePlaybackSeconds);
            Assert.Equal("pending", session.QualificationStatus);
        });
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task ConcurrentStopDelivery_GrantsExactlyOneQualifiedPlay()
    {
        RequirePostgres();
        _fixture.Clock.SetUtcNow(Utc(2028, 1, 8, 12));
        var (ownerId, _) = await CreateUserAsync("race-owner");
        var (_, listener) = await CreateUserAsync("race-listener");
        var trackId = await SeedTrackAsync(ownerId, "Concurrent Stop", duration: "2:00");

        var started = await StartAsync(listener, trackId);
        var streamId = started.GetProperty("streamId").GetGuid();
        _fixture.Clock.Advance(TimeSpan.FromSeconds(30));

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => PostStopAsync(listener, streamId, activePlaybackSeconds: 30,
                idempotencyKey: "same-stop-delivery")));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        var payloads = await Task.WhenAll(responses.Select(ReadDataAsync));
        Assert.Single(payloads, x => x.GetProperty("counted").GetBoolean());
        Assert.Equal(7, payloads.Count(x => x.GetProperty("idempotentReplay").GetBoolean()));

        await WithFreshDbAsync(async db =>
        {
            Assert.Equal(1, await db.QualifiedPlayEvents.CountAsync(x => x.TrackId == trackId));
            Assert.Equal(1, await db.StreamSessions.CountAsync(x => x.TrackId == trackId));
            var stats = await db.TrackStats.AsNoTracking().SingleAsync(x => x.TrackId == trackId);
            Assert.Equal(1, stats.PlayCount);
            Assert.Equal(1, stats.QualifiedPlayCount);
            Assert.Equal(stats.LegacyPlayCount + stats.QualifiedPlayCount, stats.PlayCount);
        });
    }

    [Fact]
    public async Task ListenerReplayInsideWindowDoesNotCount_AndNewSessionOutsideWindowDoes()
    {
        _fixture.Clock.SetUtcNow(Utc(2028, 1, 9, 12));
        var (ownerId, _) = await CreateUserAsync("window-owner");
        var (_, listener) = await CreateUserAsync("window-listener");
        var trackId = await SeedTrackAsync(ownerId, "Rolling Window", duration: "0:20");

        var firstStart = await StartAsync(listener, trackId);
        var firstStreamId = firstStart.GetProperty("streamId").GetGuid();
        _fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        var firstStop = await StopAsync(listener, firstStreamId, activePlaybackSeconds: 10);
        var firstQualifiedAt = firstStop.GetProperty("qualifiedAtUtc").GetDateTime();

        _fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        var insideWindow = await StartAsync(listener, trackId);
        Assert.Equal("already_counted", insideWindow.GetProperty("status").GetString());
        Assert.Equal(firstStreamId, insideWindow.GetProperty("streamId").GetGuid());
        var replay = await StopAsync(listener, firstStreamId, activePlaybackSeconds: 10);
        Assert.True(replay.GetProperty("idempotentReplay").GetBoolean());

        _fixture.Clock.SetUtcNow(new DateTimeOffset(firstQualifiedAt, TimeSpan.Zero).AddMinutes(60).AddSeconds(1));
        var outsideWindow = await StartAsync(listener, trackId);
        Assert.Equal("started", outsideWindow.GetProperty("status").GetString());
        Assert.NotEqual(firstStreamId, outsideWindow.GetProperty("streamId").GetGuid());
        _fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        var secondStop = await StopAsync(listener, outsideWindow.GetProperty("streamId").GetGuid(), activePlaybackSeconds: 10);
        Assert.True(secondStop.GetProperty("counted").GetBoolean());

        await WithFreshDbAsync(async db =>
        {
            Assert.Equal(2, await db.QualifiedPlayEvents.CountAsync(x => x.TrackId == trackId));
            Assert.Equal(2, (await db.TrackStats.SingleAsync(x => x.TrackId == trackId)).QualifiedPlayCount);
        });
    }

    [Fact]
    public async Task QualifiedPlaybackAppearsInScene_UsingMondayUtcInclusiveBoundary()
    {
        var sunday = new DateTimeOffset(2030, 1, 6, 23, 59, 20, TimeSpan.Zero);
        _fixture.Clock.SetUtcNow(sunday);
        var (ownerId, _) = await CreateUserAsync("scene-owner");
        var (_, listener) = await CreateUserAsync("scene-listener");
        var sundayTrack = await SeedTrackAsync(ownerId, "Sunday Track", duration: "1:00");
        var mondayTrack = await SeedTrackAsync(ownerId, "Monday Track", duration: "0:20");

        var sundayStart = await StartAsync(listener, sundayTrack);
        _fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await StopAsync(listener, sundayStart.GetProperty("streamId").GetGuid(), activePlaybackSeconds: 30);

        var sundayChartResponse = await _fixture.CreateClient().GetAsync("/api/charts/weekly");
        Assert.Equal(HttpStatusCode.OK, sundayChartResponse.StatusCode);
        var sundayChart = await ReadDataAsync(sundayChartResponse);
        Assert.Contains(sundayChart.GetProperty("entries").EnumerateArray(),
            x => x.GetProperty("trackId").GetString() == sundayTrack.ToString("D"));

        var mondayStart = await StartAsync(listener, mondayTrack);
        _fixture.Clock.Advance(TimeSpan.FromSeconds(10)); // Monday 00:00:00 UTC exactly.
        Assert.Equal(DayOfWeek.Monday, _fixture.Clock.GetUtcNow().DayOfWeek);
        Assert.Equal(TimeSpan.Zero, _fixture.Clock.GetUtcNow().TimeOfDay);
        await StopAsync(listener, mondayStart.GetProperty("streamId").GetGuid(), activePlaybackSeconds: 10);

        var mondayChartResponse = await _fixture.CreateClient().GetAsync("/api/charts/weekly");
        Assert.Equal(HttpStatusCode.OK, mondayChartResponse.StatusCode);
        var mondayChart = await ReadDataAsync(mondayChartResponse);
        var mondayEntries = mondayChart.GetProperty("entries").EnumerateArray().ToList();

        Assert.Equal(
            new DateTime(2030, 1, 7, 0, 0, 0, DateTimeKind.Unspecified),
            mondayChart.GetProperty("weekOf").GetDateTime());
        var mondayEntry = Assert.Single(mondayEntries,
            x => x.GetProperty("trackId").GetString() == mondayTrack.ToString("D"));
        var priorWeekEntry = Assert.Single(mondayEntries,
            x => x.GetProperty("trackId").GetString() == sundayTrack.ToString("D"));
        Assert.Equal(1, mondayEntry.GetProperty("weeklyQualifiedPlays").GetInt64());
        Assert.Equal(0, priorWeekEntry.GetProperty("weeklyQualifiedPlays").GetInt64());
    }

    private async Task<(string UserId, HttpClient Client)> CreateUserAsync(string prefix)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@test.com";
        var token = await _fixture.RegisterUserAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (userId, client);
    }

    private async Task<Guid> SeedTrackAsync(
        string ownerId,
        string title,
        string duration,
        string visibility = "public",
        string status = "available",
        DateTime? deletedAt = null)
    {
        var trackId = await _fixture.SeedTrackAsync(ownerId, title);
        await WithFreshDbAsync(async db =>
        {
            var track = await db.Tracks.SingleAsync(x => x.Id == trackId);
            track.Duration = duration;
            track.Visibility = visibility;
            track.Status = status;
            track.DeletedAt = deletedAt;
            await db.SaveChangesAsync();
        });
        return trackId;
    }

    private async Task SeedLegacyStatsAsync(Guid trackId, long legacyPlayCount)
    {
        await WithFreshDbAsync(async db =>
        {
            db.TrackStats.Add(new TrackStat
            {
                TrackId = trackId,
                LegacyPlayCount = legacyPlayCount,
                QualifiedPlayCount = 0,
                PlayCount = legacyPlayCount,
                UpdatedAt = _fixture.Clock.GetUtcNow().UtcDateTime
            });
            await db.SaveChangesAsync();
        });
    }

    private async Task<JsonElement> StartAsync(HttpClient client, Guid trackId, string? anonymousSessionId = null)
    {
        var response = await PostStartAsync(client, trackId, anonymousSessionId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadDataAsync(response);
    }

    private static async Task<HttpResponseMessage> PostStartAsync(
        HttpClient client,
        Guid trackId,
        string? anonymousSessionId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/stream/start")
        {
            Content = JsonContent.Create(new { trackId = trackId.ToString("D") })
        };
        if (anonymousSessionId is not null)
            request.Headers.Add("X-Cambrian-Anonymous-Session", anonymousSessionId);
        return await client.SendAsync(request);
    }

    private async Task<JsonElement> StopAsync(
        HttpClient client,
        Guid streamId,
        double? activePlaybackSeconds = null,
        double? pausedSeconds = null,
        int? seekCount = null,
        double? endingPositionSeconds = null,
        string? anonymousSessionId = null,
        string? idempotencyKey = null)
    {
        var response = await PostStopAsync(client, streamId, activePlaybackSeconds, pausedSeconds,
            seekCount, endingPositionSeconds, anonymousSessionId, idempotencyKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadDataAsync(response);
    }

    private static async Task<HttpResponseMessage> PostStopAsync(
        HttpClient client,
        Guid streamId,
        double? activePlaybackSeconds = null,
        double? pausedSeconds = null,
        int? seekCount = null,
        double? endingPositionSeconds = null,
        string? anonymousSessionId = null,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/stream/stop")
        {
            Content = JsonContent.Create(new
            {
                streamId = streamId.ToString("D"),
                activePlaybackSeconds,
                pausedSeconds,
                seekCount,
                endingPositionSeconds
            })
        };
        if (anonymousSessionId is not null)
            request.Headers.Add("X-Cambrian-Anonymous-Session", anonymousSessionId);
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("data").Clone();
    }

    private async Task WithFreshDbAsync(Func<CambrianDbContext, Task> action)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        await action(db);
    }

    private void RequirePostgres() => Assert.Equal("PostgreSQL", _fixture.DatabaseProvider);

    private static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);
}
