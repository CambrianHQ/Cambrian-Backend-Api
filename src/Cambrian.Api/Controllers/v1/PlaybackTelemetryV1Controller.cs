using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Playback;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Cambrian.Api.Controllers.v1;

[ApiController]
[Route("api/v1/playback/telemetry")]
[AllowAnonymous]
[EnableRateLimiting("playbackTelemetry")]
[RequestSizeLimit(65_536)]
public sealed class PlaybackTelemetryV1Controller : ControllerBase
{
    private static readonly IReadOnlySet<string> AllowedEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        "loadstart", "loadedmetadata", "loadeddata", "canplay", "playing", "waiting",
        "stalled", "suspend", "abort", "emptied", "error", "pause", "ended",
        "play-promise-rejected",
    };

    private static readonly IReadOnlySet<string> AllowedStatusCategories = new HashSet<string>(StringComparer.Ordinal)
    {
        "2xx", "3xx", "4xx", "5xx", "network_error", "unknown",
    };

    // Closed set mirroring the HTMLMediaElement MediaError codes plus the "none"
    // sentinel — free-form 64-char strings are not accepted here.
    private static readonly IReadOnlySet<string> AllowedMediaErrorCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "none", "MEDIA_ERR_ABORTED", "MEDIA_ERR_NETWORK", "MEDIA_ERR_DECODE", "MEDIA_ERR_SRC_NOT_SUPPORTED",
    };

    private readonly PlaybackMediaOptions _options;
    private readonly IPlaybackAnalyticsService _analytics;
    private readonly ILogger<PlaybackTelemetryV1Controller> _logger;

    public PlaybackTelemetryV1Controller(
        IOptions<PlaybackMediaOptions> options,
        IPlaybackAnalyticsService analytics,
        ILogger<PlaybackTelemetryV1Controller> logger)
    {
        _options = options.Value;
        _analytics = analytics;
        _logger = logger;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Capture([FromBody] PlaybackTelemetryBatch batch)
    {
        if (Request.ContentLength > _options.TelemetryMaxPayloadBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, Error("telemetry_payload_too_large"));
        if (batch.Events.Count is 0 || batch.Events.Count > _options.TelemetryMaxEventsPerBatch)
            return BadRequest(Error("telemetry_event_count_invalid"));

        for (var index = 0; index < batch.Events.Count; index++)
        {
            var item = batch.Events[index];
            // A JSON body like {"events":[null]} deserializes to a null element —
            // reject it as invalid instead of letting Validate NRE into a 500.
            if (item is null)
                return BadRequest(new { success = false, error = "telemetry_event_invalid", eventIndex = index });
            var validationCode = Validate(item);
            if (validationCode is not null)
                return BadRequest(new { success = false, error = validationCode, eventIndex = index });
        }

        foreach (var item in batch.Events)
        {
            _logger.LogInformation(
                "PlaybackTelemetry event={Event} trackId={TrackId} sessionId={SessionId} frontendRelease={FrontendRelease} backendRelease={BackendRelease} requestId={RequestId} browser={BrowserFamily} browserVersion={BrowserVersion} mediaError={MediaError} readyState={ReadyState} networkState={NetworkState} ttfpMs={TtfpMs} stallCount={StallCount} mediaHost={MediaHost} statusCategory={StatusCategory} eventTimestampUtc={EventTimestampUtc}",
                item.Event, item.TrackId, item.PlaybackSessionId, item.FrontendRelease,
                item.BackendRelease, item.RequestId, item.BrowserFamily, item.BrowserVersion,
                item.MediaErrorCode, item.ReadyState, item.NetworkState, item.TimeToFirstPlaybackMs,
                item.StallCount, item.MediaHost, item.ResponseStatusCategory, item.EventTimestampUtc);
        }

        // Forward the validated batch to the analytics sink. Events at this point
        // are constrained to closed enums and host-only routing data — URLs, query
        // strings, and ticket-shaped session ids were rejected above, so signed
        // playback URLs and tickets can never reach the sink. The sink is
        // best-effort: any failure is logged and the batch is still accepted.
        try
        {
            await _analytics.CaptureBrowserTelemetryAsync(batch.Events, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Playback telemetry analytics sink failed; batch of {Count} event(s) accepted anyway.",
                batch.Events.Count);
        }

        return Accepted(new { success = true, data = new { accepted = batch.Events.Count } });
    }

    private static string? Validate(PlaybackTelemetryEvent item)
    {
        if (!AllowedEvents.Contains(item.Event)) return "telemetry_event_invalid";
        if (item.TrackId == Guid.Empty) return "telemetry_track_id_invalid";
        if (!SafeOpaque(item.PlaybackSessionId, 128)) return "telemetry_session_id_invalid";
        if (!SafeText(item.FrontendRelease, 64) || !SafeText(item.BackendRelease, 64)) return "telemetry_release_invalid";
        if (!SafeText(item.RequestId, 128)) return "telemetry_request_id_invalid";
        if (!SafeText(item.BrowserFamily, 64) || !SafeText(item.BrowserVersion, 64)) return "telemetry_browser_invalid";
        if (!AllowedMediaErrorCodes.Contains(item.MediaErrorCode)) return "telemetry_media_error_invalid";
        if (item.ReadyState is < 0 or > 4 || item.NetworkState is < 0 or > 3) return "telemetry_media_state_invalid";
        if (item.TimeToFirstPlaybackMs is < 0 or > 600_000) return "telemetry_ttfp_invalid";
        if (item.StallCount is < 0 or > 10_000) return "telemetry_stall_count_invalid";
        if (!SafeHost(item.MediaHost)) return "telemetry_media_host_invalid";
        if (!AllowedStatusCategories.Contains(item.ResponseStatusCategory)) return "telemetry_status_category_invalid";
        if (item.EventTimestampUtc == default || item.EventTimestampUtc.Kind != DateTimeKind.Utc) return "telemetry_timestamp_invalid";
        return null;
    }

    private static bool SafeOpaque(string value, int maxLength) =>
        SafeText(value, maxLength)
        && !value.Contains('.')
        && !value.Contains('/')
        && !value.Contains('=');

    private static bool SafeText(string value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maxLength
        && !value.Contains("http://", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("https://", StringComparison.OrdinalIgnoreCase)
        && !value.Contains('?')
        && !value.Contains('#')
        && !value.Contains('\r')
        && !value.Contains('\n');

    private static bool SafeHost(string value) =>
        SafeText(value, 253)
        && !value.Contains('/')
        && Uri.CheckHostName(value) != UriHostNameType.Unknown;

    private static object Error(string code) => new { success = false, error = code };
}
