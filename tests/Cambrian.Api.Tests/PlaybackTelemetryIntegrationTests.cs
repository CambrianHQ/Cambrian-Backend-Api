using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.DTOs.Playback;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cambrian.Api.Tests;

public sealed class PlaybackTelemetryIntegrationTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public PlaybackTelemetryIntegrationTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ValidBoundedBatchIsAccepted()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/playback/telemetry", new
        {
            events = new[]
            {
                new
                {
                    @event = "playing",
                    trackId = Guid.NewGuid(),
                    playbackSessionId = Guid.NewGuid().ToString("N"),
                    frontendRelease = "frontend-1",
                    backendRelease = "backend-1",
                    requestId = "request-1",
                    browserFamily = "Chromium",
                    browserVersion = "126",
                    mediaErrorCode = "none",
                    readyState = 4,
                    networkState = 1,
                    timeToFirstPlaybackMs = 250,
                    stallCount = 0,
                    mediaHost = "api.cambrianmusic.com",
                    responseStatusCategory = "2xx",
                    eventTimestampUtc = DateTime.UtcNow,
                }
            }
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task FullUrlsTicketsAndUnknownPropertiesAreRejected()
    {
        using var client = _fixture.CreateClient();
        using var unsafePayload = await client.PostAsJsonAsync("/api/v1/playback/telemetry", new
        {
            events = new[]
            {
                new
                {
                    @event = "error",
                    trackId = Guid.NewGuid(),
                    playbackSessionId = "payload.signature",
                    frontendRelease = "frontend-1",
                    backendRelease = "backend-1",
                    requestId = "request-1",
                    browserFamily = "Chromium",
                    browserVersion = "126",
                    mediaErrorCode = "MEDIA_ERR_SRC_NOT_SUPPORTED",
                    readyState = 0,
                    networkState = 3,
                    timeToFirstPlaybackMs = (int?)null,
                    stallCount = 1,
                    mediaHost = "https://api.cambrianmusic.com/stream/id/audio?ticket=secret",
                    responseStatusCategory = "4xx",
                    eventTimestampUtc = DateTime.UtcNow,
                }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, unsafePayload.StatusCode);

        const string unknown = """
            {"events":[{"event":"playing","trackId":"00000000-0000-0000-0000-000000000001","playbackSessionId":"abc","frontendRelease":"f","backendRelease":"b","requestId":"r","browserFamily":"c","browserVersion":"1","mediaErrorCode":"none","readyState":4,"networkState":1,"stallCount":0,"mediaHost":"example.com","responseStatusCategory":"2xx","eventTimestampUtc":"2026-07-16T20:00:00Z","ticket":"secret"}]}
            """;
        using var unknownResponse = await client.PostAsync("/api/v1/playback/telemetry",
            new StringContent(unknown, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, unknownResponse.StatusCode);
    }

    [Fact]
    public async Task NullBatchElementIsRejectedNotServerError()
    {
        // Regression test: {"events":[null]} used to NRE inside per-event
        // validation and surface as a 500 — it must be a structured 400.
        using var client = _fixture.CreateClient();
        using var response = await client.PostAsync("/api/v1/playback/telemetry",
            new StringContent("""{"events":[null]}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("telemetry_event_invalid", (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString());
    }

    [Fact]
    public async Task EmptyAndOversizedBatchesAreRejected()
    {
        using var client = _fixture.CreateClient();

        using var empty = await client.PostAsJsonAsync("/api/v1/playback/telemetry",
            new { events = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("telemetry_event_count_invalid", (await empty.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString());

        // TelemetryMaxEventsPerBatch defaults to 50 — 51 events is one over.
        using var oversized = await client.PostAsJsonAsync("/api/v1/playback/telemetry",
            new { events = Enumerable.Range(0, 51).Select(_ => ValidTelemetryEvent()).ToArray() });
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Equal("telemetry_event_count_invalid", (await oversized.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("event", "bogus", "telemetry_event_invalid")]
    [InlineData("readyState", 5, "telemetry_media_state_invalid")]
    [InlineData("networkState", 9, "telemetry_media_state_invalid")]
    [InlineData("responseStatusCategory", "1xx", "telemetry_status_category_invalid")]
    [InlineData("responseStatusCategory", "bogus", "telemetry_status_category_invalid")]
    [InlineData("mediaErrorCode", "SOMETHING_ELSE", "telemetry_media_error_invalid")]
    public async Task OutOfRangeFieldValuesAreRejectedWithFieldSpecificCodes(
        string field, object value, string expectedError)
    {
        var telemetryEvent = ValidTelemetryEvent();
        telemetryEvent[field] = value;

        using var client = _fixture.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/playback/telemetry",
            new { events = new[] { telemetryEvent } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedError, (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("MEDIA_ERR_ABORTED")]
    [InlineData("MEDIA_ERR_NETWORK")]
    [InlineData("MEDIA_ERR_DECODE")]
    [InlineData("MEDIA_ERR_SRC_NOT_SUPPORTED")]
    public async Task EveryAllowedMediaErrorCodeIsAccepted(string mediaErrorCode)
    {
        var telemetryEvent = ValidTelemetryEvent();
        telemetryEvent["mediaErrorCode"] = mediaErrorCode;

        using var client = _fixture.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/playback/telemetry",
            new { events = new[] { telemetryEvent } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // Mutable copy of the ValidBoundedBatchIsAccepted payload so single-field
    // variants can be produced without repeating the whole event shape.
    internal static Dictionary<string, object?> ValidTelemetryEvent() => new()
    {
        ["event"] = "playing",
        ["trackId"] = Guid.NewGuid(),
        ["playbackSessionId"] = Guid.NewGuid().ToString("N"),
        ["frontendRelease"] = "frontend-1",
        ["backendRelease"] = "backend-1",
        ["requestId"] = "request-1",
        ["browserFamily"] = "Chromium",
        ["browserVersion"] = "126",
        ["mediaErrorCode"] = "none",
        ["readyState"] = 4,
        ["networkState"] = 1,
        ["timeToFirstPlaybackMs"] = 250,
        ["stallCount"] = 0,
        ["mediaHost"] = "api.cambrianmusic.com",
        ["responseStatusCategory"] = "2xx",
        ["eventTimestampUtc"] = DateTime.UtcNow,
    };
}

public sealed class PlaybackTelemetrySinkFailureTests
    : IClassFixture<PlaybackTelemetrySinkFailureTests.FailingSinkFixture>
{
    private readonly FailingSinkFixture _fixture;

    public PlaybackTelemetrySinkFailureTests(FailingSinkFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AnalyticsSinkFailureStillAcceptsValidBatch()
    {
        // The analytics sink is best-effort: a throwing implementation must be
        // reached and swallowed, never turn a valid batch into an error response.
        using var client = _fixture.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/playback/telemetry",
            new { events = new[] { PlaybackTelemetryIntegrationTests.ValidTelemetryEvent() } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(_fixture.Sink.Calls > 0);
    }

    public sealed class FailingSinkFixture : CambrianApiFixture
    {
        public ThrowingPlaybackAnalyticsSink Sink =>
            (ThrowingPlaybackAnalyticsSink)Services.GetRequiredService<IPlaybackAnalyticsService>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPlaybackAnalyticsService>();
                services.AddSingleton<IPlaybackAnalyticsService, ThrowingPlaybackAnalyticsSink>();
            });
        }
    }

    public sealed class ThrowingPlaybackAnalyticsSink : IPlaybackAnalyticsService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task CaptureAcceptedAsync(PlaybackAnalyticsEvent playEvent, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task CaptureBrowserTelemetryAsync(
            IReadOnlyList<PlaybackTelemetryEvent> events, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("Telemetry analytics sink is down.");
        }
    }
}
