using System.Net.Http.Json;
using Cambrian.Application.DTOs.Playback;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cambrian.Infrastructure.Analytics;

/// <summary>Best-effort mirror emitted only after PostgreSQL accepts the play.</summary>
public sealed class PostHogPlaybackAnalyticsService : IPlaybackAnalyticsService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PostHogPlaybackAnalyticsService> _logger;

    public PostHogPlaybackAnalyticsService(
        HttpClient http,
        IConfiguration configuration,
        ILogger<PostHogPlaybackAnalyticsService> logger)
    {
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task CaptureAcceptedAsync(PlaybackAnalyticsEvent playEvent, CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        var host = ResolveHost();
        var eventId = playEvent.EventId.ToString("D");
        var properties = new Dictionary<string, object?>
        {
            ["qualified_play_event_id"] = eventId,
            ["track_id"] = playEvent.TrackId.ToString("D"),
            ["creator_id"] = playEvent.CreatorId,
            ["qualified_at_utc"] = playEvent.QualifiedAtUtc,
            ["active_playback_seconds"] = playEvent.ActivePlaybackSeconds,
            ["threshold_seconds"] = playEvent.ThresholdSeconds,
            ["is_anonymous"] = playEvent.IsAnonymous,
            ["$insert_id"] = eventId
        };
        var payload = new
        {
            api_key = apiKey,
            @event = "qualified_play_accepted",
            distinct_id = playEvent.DistinctId,
            properties
        };

        try
        {
            using var response = await _http.PostAsJsonAsync($"{host}/capture/", payload, ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("PostHog qualified-play mirror returned status {StatusCode} for eventId:{EventId}.",
                    (int)response.StatusCode, playEvent.EventId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PostHog qualified-play mirror failed open for eventId:{EventId}.", playEvent.EventId);
        }
    }

    /// <summary>
    /// Forwards a validated browser playback-telemetry batch to PostHog's batch
    /// endpoint. Events reaching this sink already passed endpoint validation
    /// (closed enums, host-only media routing, opaque non-ticket session ids), so
    /// no signed URL or ticket material is ever serialized here. Fails open.
    /// </summary>
    public async Task CaptureBrowserTelemetryAsync(IReadOnlyList<PlaybackTelemetryEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0)
            return;

        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        var host = ResolveHost();
        var payload = new
        {
            api_key = apiKey,
            batch = events.Select(item => new
            {
                @event = "playback_browser_telemetry",
                distinct_id = item.PlaybackSessionId,
                timestamp = item.EventTimestampUtc,
                properties = new Dictionary<string, object?>
                {
                    ["media_event"] = item.Event,
                    ["track_id"] = item.TrackId.ToString("D"),
                    ["playback_session_id"] = item.PlaybackSessionId,
                    ["frontend_release"] = item.FrontendRelease,
                    ["backend_release"] = item.BackendRelease,
                    ["request_id"] = item.RequestId,
                    ["browser_family"] = item.BrowserFamily,
                    ["browser_version"] = item.BrowserVersion,
                    ["media_error_code"] = item.MediaErrorCode,
                    ["ready_state"] = item.ReadyState,
                    ["network_state"] = item.NetworkState,
                    ["time_to_first_playback_ms"] = item.TimeToFirstPlaybackMs,
                    ["stall_count"] = item.StallCount,
                    ["media_host"] = item.MediaHost,
                    ["response_status_category"] = item.ResponseStatusCategory,
                },
            }).ToArray(),
        };

        try
        {
            using var response = await _http.PostAsJsonAsync($"{host}/batch/", payload, ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("PostHog playback-telemetry mirror returned status {StatusCode} for a batch of {Count} event(s).",
                    (int)response.StatusCode, events.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PostHog playback-telemetry mirror failed open for a batch of {Count} event(s).", events.Count);
        }
    }

    private string? ResolveApiKey() =>
        _configuration["POSTHOG_API_KEY"]
            ?? _configuration["PostHog:ApiKey"]
            ?? Environment.GetEnvironmentVariable("POSTHOG_API_KEY");

    private string ResolveHost() =>
        (_configuration["POSTHOG_HOST"]
            ?? _configuration["PostHog:Host"]
            ?? Environment.GetEnvironmentVariable("POSTHOG_HOST")
            ?? "https://app.posthog.com").TrimEnd('/');
}
