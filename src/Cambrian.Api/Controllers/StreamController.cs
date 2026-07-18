using System.Security.Claims;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Playback;
using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cambrian.Api.Controllers;

[Route("stream")]
public class StreamController : BaseController
{
    private readonly ITrackRepository _tracks;
    private readonly IObjectStorage _storage;
    private readonly ITrackVisibilityPolicy _visibility;
    private readonly ILogger<StreamController> _logger;
    private readonly IPlaybackTrackingService _playback;
    private readonly IPlaybackAccessService? _playbackAccess;
    private readonly IPlaybackTicketService? _tickets;
    private readonly IMediaProbeSignatureService? _probeSignatures;
    private readonly PlaybackMediaOptions _mediaOptions;
    private readonly string _backendRelease;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public StreamController(
        ITrackRepository tracks,
        IObjectStorage storage,
        ITrackVisibilityPolicy visibility,
        ILogger<StreamController> logger,
        IPlaybackTrackingService playback,
        IPlaybackAccessService playbackAccess,
        IPlaybackTicketService tickets,
        IMediaProbeSignatureService probeSignatures,
        IOptions<PlaybackMediaOptions> mediaOptions)
    {
        _tracks = tracks;
        _storage = storage;
        _visibility = visibility;
        _logger = logger;
        _playback = playback;
        _playbackAccess = playbackAccess;
        _tickets = tickets;
        _probeSignatures = probeSignatures;
        _mediaOptions = mediaOptions.Value;
        _backendRelease = ResolveBackendRelease(_mediaOptions.BackendRelease);
    }

    public StreamController(
        ITrackRepository tracks,
        IObjectStorage storage,
        ITrackVisibilityPolicy visibility,
        ILogger<StreamController> logger,
        IPlaybackTrackingService playback)
    {
        _tracks = tracks;
        _storage = storage;
        _visibility = visibility;
        _logger = logger;
        _playback = playback;
        _mediaOptions = new PlaybackMediaOptions();
        _backendRelease = ResolveBackendRelease(_mediaOptions.BackendRelease);
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int take = 20)
    {
        take = Math.Clamp(take, 1, 100);
        var tracks = await _tracks.BrowseAsync();
        var result = new List<object>();
        foreach (var t in tracks.Take(take))
        {
            result.Add(new
            {
                id = t.Id.ToString(),
                title = t.Title,
                artist = t.Creator?.DisplayName ?? "Unknown",
                genre = t.Genre,
                duration = t.Duration,
                audioUrl = ResolveAbsoluteUrl($"/stream/{t.Id}/audio")
            });
        }

        return OkResponse(result);
    }

    /// <summary>
    /// Returns a streamable URL for the given track. Open to all authenticated users
    /// (no purchase check) by design — this is the marketplace discovery/preview model.
    /// Full-quality downloads require a verified purchase (see DownloadController).
    /// </summary>
    [Authorize]
    [HttpGet("{trackId}")]
    public async Task<IActionResult> Stream(string trackId)
    {
        if (!Guid.TryParse(trackId, out var id))
            return ErrorResponse("trackId must be a valid GUID.");

        var track = await _tracks.GetByIdAsync(id);
        if (track is null)
            return NotFoundResponse("Track not found.");

        // C4: enforce visibility via shared policy — single source of truth.
        var streamUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!_visibility.CanAccess(track.Visibility, track.CreatorId, streamUserId, User.IsInRole("Admin")))
            return NotFoundResponse("Track not found.");

        if (_playbackAccess is null || _tickets is null)
            return PlaybackFailure(trackId, "playback_url_generation_failed", "Playback is temporarily unavailable.", "configuration", StatusCodes.Status503ServiceUnavailable);

        var access = await _playbackAccess.PrepareAsync(id, streamUserId, User.IsInRole("Admin"), HttpContext.RequestAborted);
        if (access.Outcome != PlaybackAccessOutcome.Ready)
        {
            var status = access.Outcome == PlaybackAccessOutcome.NotReady
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status503ServiceUnavailable;
            return PlaybackFailure(trackId, access.ErrorCode ?? "playback_url_generation_failed", access.SafeMessage ?? "Playback is temporarily unavailable.", "media_state", status);
        }

        try
        {
            var issued = _tickets.Issue(id, access.AuthorizedUserId);
            var streamUrl = ResolveAbsoluteUrl($"/stream/{id:D}/audio?ticket={Uri.EscapeDataString(issued.Ticket)}");
            Response.Headers.CacheControl = "private, no-store";
            return OkResponse(new { trackId, streamUrl, expiresAt = issued.ExpiresAtUtc });
        }
        catch (InvalidOperationException)
        {
            return PlaybackFailure(trackId, "playback_url_generation_failed", "Playback is temporarily unavailable.", "configuration", StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Proxies audio from storage (R2/S3/local) to the client with full HTTP Range
    /// support. Safari's &lt;audio&gt; element and iOS AVPlayer both probe for range
    /// support with a small initial Range request and refuse to play when the
    /// response is missing Content-Length or Content-Range — they surface this as
    /// "Unable to load audio file, may be unavailable or corrupted". We forward the
    /// client's Range header to the origin so S3/R2 returns a 206 Partial Content
    /// response with the right headers, and we echo those headers back to the
    /// client. Open to anonymous users — this is the marketplace discovery/preview
    /// model. Full-quality downloads require a verified purchase (see DownloadController).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{trackId}/audio")]
    [HttpHead("{trackId}/audio")]
    [EnableRateLimiting("playback")]
    public async Task<IActionResult> StreamAudio(string trackId)
    {
        ApplyPlaybackResponseHeaders();
        if (!Guid.TryParse(trackId, out var id))
            return ErrorResponse("trackId must be a valid GUID.");

        string? authoritativeObjectKey = null;
        Cambrian.Domain.Entities.Track? track = null;
        var probeAuthorized = _probeSignatures?.Validate(
            Request.Headers["X-Cambrian-Media-Probe"].FirstOrDefault(), id) == true;
        var suppliedTicket = Request.Query["ticket"].FirstOrDefault();

        if (probeAuthorized && _playbackAccess is not null)
        {
            var target = await _playbackAccess.PrepareTicketStreamAsync(id, true, HttpContext.RequestAborted);
            if (target.Outcome != PlaybackAccessOutcome.Ready)
            {
                var status = target.Outcome == PlaybackAccessOutcome.NotFound
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status409Conflict;
                return PlaybackFailure(trackId, target.ErrorCode ?? "track_not_ready", target.SafeMessage ?? "Track media is not ready.", "media_state", status);
            }
            authoritativeObjectKey = target.ObjectKey;
        }
        else if (!string.IsNullOrWhiteSpace(suppliedTicket) && _tickets is not null && _playbackAccess is not null)
        {
            var ticket = _tickets.Validate(suppliedTicket, id);
            if (!ticket.IsValid)
                return PlaybackFailure(trackId, ticket.FailureCode ?? "ticket_invalid", "Playback authorization is invalid or expired.", "authorization", StatusCodes.Status401Unauthorized);

            var target = await _playbackAccess.PrepareTicketStreamAsync(id, false, HttpContext.RequestAborted);
            if (target.Outcome != PlaybackAccessOutcome.Ready)
            {
                var status = target.Outcome == PlaybackAccessOutcome.NotFound
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status409Conflict;
                return PlaybackFailure(trackId, target.ErrorCode ?? "track_not_ready", target.SafeMessage ?? "Track media is not ready.", "media_state", status);
            }
            authoritativeObjectKey = target.ObjectKey;
        }
        else
        {
            // Enforcement rejects before any lookup so 401-vs-404 cannot be used to
            // enumerate which track IDs exist.
            if (_mediaOptions.ReadinessEnforcementEnabled || !_mediaOptions.LegacyPublicStreamEnabled)
                return PlaybackFailure(trackId, "unauthorized", "A valid playback ticket is required.", "authorization", StatusCodes.Status401Unauthorized);

            track = await _tracks.GetByIdAsync(id);
            if (track is null)
                return NotFoundResponse("Track not found.");

            var audioUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!_visibility.CanAccess(track.Visibility, track.CreatorId, audioUserId, User.IsInRole("Admin")))
                return NotFoundResponse("Track not found.");
        }

        if (HttpMethods.IsHead(Request.Method))
            return await StreamAudioHeadAsync(trackId, authoritativeObjectKey, track);

        var rangeHeader = Request.Headers.Range.ToString();
        var hasRange = !string.IsNullOrWhiteSpace(rangeHeader);
        _logger.LogInformation(
            "StreamAudio: streaming trackId={TrackId} via backend proxy ranged={Ranged}",
            trackId, hasRange);

        StorageFile? file;
        try
        {
            if (authoritativeObjectKey is not null)
            {
                // The validated TrackMedia key is authoritative — never substitute a
                // guessed fallback object on the ticketed/probe path.
                file = hasRange
                    ? await _storage.OpenReadAsync(authoritativeObjectKey, rangeHeader)
                    : await _storage.OpenReadAsync(authoritativeObjectKey);
            }
            else
            {
                (_, file) = await OpenPlayableAudioAsync(track!.AudioUrl, track.CambrianTrackId, track.Id, hasRange ? rangeHeader : null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "EVENT: playback_url_failed trackId:{TrackId} category:storage correlationId:{CorrelationId}",
                trackId, HttpContext.TraceIdentifier);
            return PlaybackFailure(trackId, "storage_unavailable", "Playback is temporarily unavailable.", "storage", StatusCodes.Status503ServiceUnavailable);
        }
        if (file is null)
        {
            if (authoritativeObjectKey is null
                && IsSeedTrack(track!.CambrianTrackId)
                && !_mediaOptions.ReadinessEnforcementEnabled)
            {
                _logger.LogWarning("StreamAudio: serving generated silent placeholder for seed trackId={TrackId}", trackId);
                var placeholder = SilentMp3Generator.Generate();
                Response.Headers["Accept-Ranges"] = "bytes";
                return File(placeholder, "audio/mpeg", enableRangeProcessing: true);
            }
            return PlaybackFailure(trackId, "media_object_missing", "Audio file not found on storage.", "missing_object", StatusCodes.Status503ServiceUnavailable);
        }

        if (file.IsRangeNotSatisfiable)
        {
            using (file)
            {
                Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                Response.Headers["Accept-Ranges"] = "bytes";
                if (file.TotalLength.HasValue)
                    Response.Headers["Content-Range"] = $"bytes */{file.TotalLength.Value}";
                Response.ContentLength = 0;
            }
            return new EmptyResult();
        }

        if (file.Stream.CanSeek)
        {
            Response.Headers["Accept-Ranges"] = "bytes";
            return File(file.Stream, file.ContentType, enableRangeProcessing: true);
        }

        using (file)
        {
            Response.Headers["Accept-Ranges"] = "bytes";
            Response.ContentType = file.ContentType;
            if (file.IsPartialContent)
            {
                Response.StatusCode = StatusCodes.Status206PartialContent;
                if (!string.IsNullOrEmpty(file.ContentRange))
                    Response.Headers["Content-Range"] = file.ContentRange;
            }
            if (file.Length.HasValue && file.Length.Value >= 0)
                Response.ContentLength = file.Length.Value;
            await file.Stream.CopyToAsync(Response.Body, HttpContext.RequestAborted);
        }

        return new EmptyResult();
    }

    private async Task<IActionResult> StreamAudioHeadAsync(string trackId, string? authoritativeObjectKey, Cambrian.Domain.Entities.Track? track)
    {
        try
        {
            StorageObjectMetadata? metadata = null;
            if (authoritativeObjectKey is not null)
            {
                metadata = await _storage.GetMetadataAsync(authoritativeObjectKey, HttpContext.RequestAborted);
            }
            else
            {
                // Mirror the GET candidate order so HEAD reports the same object GET
                // would serve, including the seed placeholder fallback.
                foreach (var candidate in GetAudioCandidates(track!.AudioUrl, track.CambrianTrackId))
                {
                    metadata = await _storage.GetMetadataAsync(candidate, HttpContext.RequestAborted);
                    if (metadata is not null)
                        break;
                }
                if (metadata is null && IsSeedTrack(track.CambrianTrackId) && !_mediaOptions.ReadinessEnforcementEnabled)
                {
                    Response.Headers["Accept-Ranges"] = "bytes";
                    Response.ContentType = "audio/mpeg";
                    Response.ContentLength = SilentMp3Generator.Generate().Length;
                    return new EmptyResult();
                }
            }

            if (metadata is null)
                return PlaybackFailure(trackId, "media_object_missing", "Audio file not found on storage.", "missing_object", StatusCodes.Status503ServiceUnavailable);
            Response.Headers["Accept-Ranges"] = "bytes";
            Response.ContentType = metadata.ContentType ?? "application/octet-stream";
            Response.ContentLength = metadata.SizeBytes;
            return new EmptyResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            return PlaybackFailure(trackId, "storage_unavailable", "Playback storage is temporarily unavailable.", "storage", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private void ApplyPlaybackResponseHeaders()
    {
        Response.Headers["X-Backend-Release"] = _backendRelease;
        // Always explicit: audio bytes and playback errors must never be shared-cached,
        // and the same URL serves different bytes after a re-upload.
        Response.Headers.CacheControl = "private, no-store";
    }

    private static string ResolveBackendRelease(string configured) =>
        !string.IsNullOrWhiteSpace(configured) && configured != "unknown"
            ? configured
            : Environment.GetEnvironmentVariable("GIT_COMMIT")
                ?? Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT")
                ?? "unknown";

    /// <summary>
    /// Creates or resumes a pending playback session. A start never increments play count;
    /// qualification occurs only on /stream/stop with active-playback evidence.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("start")]
    [EnableRateLimiting("playback")]
    [ProducesResponseType(typeof(Cambrian.Api.Common.ApiResponse<PlaybackStartResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PlaybackErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PlaybackErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Start([FromBody] PlaybackStartRequest? body = null, [FromQuery] string? trackId = null)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var rawTrackId = body?.TrackId ?? trackId;
        if (string.IsNullOrWhiteSpace(rawTrackId) || !Guid.TryParse(rawTrackId, out var parsedTrackId))
            return PlaybackErrorResult(400, "invalid_track_id", "trackId must be a valid GUID.");

        var anonymousSession = string.IsNullOrEmpty(userId) ? ResolveAnonymousSession(create: true) : null;
        var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault() ?? body?.ClientEventId;

        try
        {
            var result = await _playback.StartAsync(new PlaybackStartCommand(
                parsedTrackId,
                userId,
                User.IsInRole("Admin"),
                anonymousSession,
                Request.Headers.UserAgent.ToString(),
                idempotencyKey), HttpContext.RequestAborted);

            return OkResponse(new PlaybackStartResponse(
                result.PlaybackSessionId?.ToString("D"),
                result.Status,
                result.QualificationThresholdSeconds,
                result.DeduplicationWindowMinutes,
                result.ServerTimeUtc,
                result.AnonymousSessionAccepted));
        }
        catch (PlaybackTrackingException ex)
        {
            return PlaybackErrorResult(ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private async Task<(string? AudioKey, StorageFile? File)> OpenPlayableAudioAsync(string? audioUrl, string? cambrianTrackId, Guid trackId, string? rangeHeader = null)
    {
        foreach (var candidate in GetAudioCandidates(audioUrl, cambrianTrackId))
        {
            var file = string.IsNullOrWhiteSpace(rangeHeader)
                ? await _storage.OpenReadAsync(candidate)
                : await _storage.OpenReadAsync(candidate, rangeHeader);

            if (file is not null)
            {
                if (!string.Equals(audioUrl, candidate, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "StreamAudio: using seeded fallback audio for trackId={TrackId}",
                        trackId);
                }

                return (candidate, file);
            }
        }

        return (null, null);
    }

    private IActionResult PlaybackFailure(string trackId, string code, string message, string category, int statusCode)
    {
        Cambrian.Application.Observability.CambrianMetrics.PlaybackUrlFailed.Add(1);
        _logger.LogWarning(
            "EVENT: playback_url_failed trackId:{TrackId} category:{Category} code:{Code} correlationId:{CorrelationId}",
            trackId, category, code, HttpContext.TraceIdentifier);
        return StatusCode(statusCode, new
        {
            success = false,
            error = new { code, message, trackId, category, correlationId = HttpContext.TraceIdentifier }
        });
    }

    private static IEnumerable<string> GetAudioCandidates(string? audioUrl, string? cambrianTrackId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var configured = audioUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(configured) && seen.Add(configured))
            yield return configured;

        var seededFallback = BuildSeedFallbackAudioKey(cambrianTrackId);
        if (!string.IsNullOrWhiteSpace(seededFallback) && seen.Add(seededFallback))
            yield return seededFallback;
    }

    private static bool IsSeedTrack(string? cambrianTrackId) =>
        !string.IsNullOrEmpty(cambrianTrackId) &&
        cambrianTrackId.StartsWith("CAMB-TRK-SEED", StringComparison.OrdinalIgnoreCase);

    private static string? BuildSeedFallbackAudioKey(string? cambrianTrackId)
    {
        if (string.IsNullOrWhiteSpace(cambrianTrackId)
            || !cambrianTrackId.StartsWith("CAMB-TRK-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var slug = cambrianTrackId["CAMB-TRK-".Length..].Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(slug) ? null : $"tracks/demo-{slug}.mp3";
    }

    [AllowAnonymous]
    [HttpPost("stop")]
    [EnableRateLimiting("playback")]
    [ProducesResponseType(typeof(Cambrian.Api.Common.ApiResponse<PlaybackStopResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PlaybackErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PlaybackErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PlaybackErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Stop([FromBody] PlaybackStopRequest? body = null, [FromQuery] string? streamId = null)
    {
        var rawStreamId = body?.StreamId ?? streamId;
        if (string.IsNullOrWhiteSpace(rawStreamId) || !Guid.TryParse(rawStreamId, out var sid))
            return PlaybackErrorResult(400, "invalid_stream_id", "streamId must be a valid GUID.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var anonymousSession = string.IsNullOrEmpty(userId) ? ResolveAnonymousSession(create: false) : null;
        var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault() ?? body?.ClientEventId;

        try
        {
            var result = await _playback.StopAsync(new PlaybackStopCommand(
                sid,
                userId,
                User.IsInRole("Admin"),
                anonymousSession,
                Request.Headers.UserAgent.ToString(),
                idempotencyKey,
                body?.ActivePlaybackSeconds,
                body?.PausedSeconds,
                body?.SeekCount,
                body?.EndingPositionSeconds), HttpContext.RequestAborted);

            return OkResponse(new PlaybackStopResponse(
                result.PlaybackSessionId.ToString("D"),
                result.Status,
                result.Qualified,
                result.Counted,
                result.IdempotentReplay,
                result.ActivePlaybackSeconds,
                result.QualificationThresholdSeconds,
                result.QualifiedAtUtc,
                result.LifetimePlayCount,
                result.ServerTimeUtc));
        }
        catch (PlaybackTrackingException ex)
        {
            return PlaybackErrorResult(ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private IActionResult PlaybackErrorResult(int statusCode, string code, string message) =>
        StatusCode(statusCode, new PlaybackErrorResponse(false,
            new PlaybackError(code, message, HttpContext.TraceIdentifier)));

    private string? ResolveAnonymousSession(bool create)
    {
        const string headerName = "X-Cambrian-Anonymous-Session";
        const string cookieName = "cambrian_playback_session";

        var supplied = Request.Headers[headerName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(supplied))
            return supplied;

        if (Request.Cookies.TryGetValue(cookieName, out var cookie) && !string.IsNullOrWhiteSpace(cookie))
            return cookie;

        if (!create) return null;

        var generated = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();
        Response.Cookies.Append(cookieName, generated, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = Request.IsHttps ? SameSiteMode.None : SameSiteMode.Lax,
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(365),
            Path = "/"
        });
        return generated;
    }
}
