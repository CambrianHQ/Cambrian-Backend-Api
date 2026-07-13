using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("stream")]
public class StreamController : BaseController
{
    private readonly ITrackRepository _tracks;
    private readonly IObjectStorage _storage;
    private readonly IStreamRepository _streams;
    private readonly ITrackVisibilityPolicy _visibility;
    private readonly IMemoryCache _cache;
    private readonly ILogger<StreamController> _logger;

    public StreamController(ITrackRepository tracks, IObjectStorage storage, IStreamRepository streams, ITrackVisibilityPolicy visibility, IMemoryCache cache, ILogger<StreamController> logger)
    {
        _tracks = tracks;
        _storage = storage;
        _streams = streams;
        _visibility = visibility;
        _cache = cache;
        _logger = logger;
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

        try
        {
            var (audioKey, audioFile) = await OpenPlayableAudioAsync(track.AudioUrl, track.CambrianTrackId, track.Id);
            audioFile?.Dispose();
            if (string.IsNullOrEmpty(audioKey))
                return PlaybackFailure(trackId, "audio_object_missing", "Audio file not found on storage.", "missing_object", StatusCodes.Status404NotFound);

            var streamUrl = _storage.GenerateSignedUrl(audioKey);
            var expiresAt = _storage.SignedUrlLifetime is { } lifetime ? DateTime.UtcNow.Add(lifetime) : (DateTime?)null;
            Cambrian.Application.Observability.CambrianMetrics.StreamSignedUrlIssued.Add(1);
            return OkResponse(new { trackId, streamUrl = ResolveAbsoluteUrl(streamUrl), expiresAt });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "EVENT: playback_url_failed trackId:{TrackId} category:storage correlationId:{CorrelationId}",
                trackId, HttpContext.TraceIdentifier);
            return PlaybackFailure(trackId, "playback_url_failed", "Playback is temporarily unavailable.", "storage", StatusCodes.Status503ServiceUnavailable);
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
    public async Task<IActionResult> StreamAudio(string trackId)
    {
        if (!Guid.TryParse(trackId, out var id))
            return ErrorResponse("trackId must be a valid GUID.");

        var track = await _tracks.GetByIdAsync(id);
        if (track is null)
            return NotFoundResponse("Track not found.");

        // C4: enforce visibility via shared policy (anonymous users allowed for public tracks).
        var audioUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!_visibility.CanAccess(track.Visibility, track.CreatorId, audioUserId, User.IsInRole("Admin")))
            return NotFoundResponse("Track not found.");

        var rangeHeader = Request.Headers.Range.ToString();
        var hasRange = !string.IsNullOrWhiteSpace(rangeHeader);

        _logger.LogInformation(
            "StreamAudio: streaming trackId={TrackId} via backend proxy range={Range}",
            trackId, hasRange ? rangeHeader : "(none)");

        // Always proxy audio through the backend to avoid CORS issues with R2/S3.
        // The browser's <audio> element follows redirects but cross-origin R2 URLs
        // lack CORS headers, causing playback to fail silently.
        //
        // Call the single-arg overload when no Range header is present so callers
        // (and tests) that only stub the no-range signature keep working; the
        // two-arg overload is only used when the client actually sent a Range.
        StorageFile? file;
        try
        {
            (_, file) = await OpenPlayableAudioAsync(track.AudioUrl, track.CambrianTrackId, track.Id, hasRange ? rangeHeader : null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "EVENT: playback_url_failed trackId:{TrackId} category:storage correlationId:{CorrelationId}",
                trackId, HttpContext.TraceIdentifier);
            return PlaybackFailure(trackId, "playback_url_failed", "Playback is temporarily unavailable.", "storage", StatusCodes.Status503ServiceUnavailable);
        }
        if (file is null)
        {
            // For demo/seed tracks whose placeholder audio was never uploaded to
            // S3 (common on staging after a fresh deploy), generate a valid silent
            // MP3 on-the-fly so the frontend audio player doesn't break.
            if (IsSeedTrack(track.CambrianTrackId))
            {
                _logger.LogWarning(
                    "StreamAudio: serving generated silent placeholder for seed trackId={TrackId}",
                    trackId);
                var placeholder = SilentMp3Generator.Generate();
                Response.Headers["Accept-Ranges"] = "bytes";
                return File(placeholder, "audio/mpeg", enableRangeProcessing: true);
            }

            return PlaybackFailure(trackId, "audio_object_missing", "Audio file not found on storage.", "missing_object", StatusCodes.Status404NotFound);
        }

        // If the storage layer returned a seekable stream (e.g. LocalObjectStorage
        // in development) let ASP.NET Core handle range processing — it emits the
        // correct Content-Length / Content-Range / 206 headers automatically.
        //
        // Deliberately NOT wrapping `file` in `using` on this path: FileStreamResult
        // takes ownership of the underlying stream and disposes it after writing
        // the response. Disposing `file` here would close the stream before the
        // framework reads it. LocalObjectStorage's StorageFile has no OwnedResource,
        // so there is nothing else to clean up on the seekable branch.
        if (file.Stream.CanSeek)
        {
            Response.Headers["Accept-Ranges"] = "bytes";
            return File(file.Stream, file.ContentType, enableRangeProcessing: true);
        }

        // Non-seekable stream (S3/R2 HTTP proxy). We own the lifetime of `file`
        // here: write the response manually so Content-Length, Accept-Ranges, and
        // — for 206 — Content-Range are all present (without these, Safari and
        // iOS AVPlayer refuse to play), then let the using-block dispose both the
        // stream and the owned HttpResponseMessage on normal completion or on
        // exception (e.g. client disconnect mid-transfer).
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

    /// <summary>
    /// Records the start of a play. Anonymous is allowed (F7/F1): logged-out listeners'
    /// plays are counted, attributed to no user. To keep anonymous counts honest, they are
    /// rate-limited to one counted play per (track, client IP) per hour, so refreshes and
    /// fire-and-forget retries can't inflate the count. This endpoint is fast (in-process
    /// dedup + a single insert) and must never gate playback — the frontend fires it
    /// independently of the &lt;audio&gt; element.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StreamStartRequest? body = null, [FromQuery] string? trackId = null)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var rawTrackId = body?.TrackId ?? trackId;
        if (string.IsNullOrWhiteSpace(rawTrackId) || !Guid.TryParse(rawTrackId, out var parsedTrackId))
            return ErrorResponse("trackId must be a valid GUID.");

        var track = await _tracks.GetByIdAsync(parsedTrackId);
        if (track is null)
            return NotFoundResponse("Track not found.");

        // C4: enforce visibility via shared policy (anonymous users allowed for public tracks).
        if (!_visibility.CanAccess(track.Visibility, track.CreatorId, userId, User.IsInRole("Admin")))
            return NotFoundResponse("Track not found.");

        // Anonymous plays are not deduped by the repository (no user to attribute to), so
        // rate-limit them here: at most one counted play per (track, client IP) per hour.
        // In-process cache — no extra DB round-trip on the hot path. Authenticated plays
        // fall through to the repository's own 30-second debounce.
        if (string.IsNullOrEmpty(userId))
        {
            var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var dedupeKey = $"anonplay:{parsedTrackId}:{clientIp}";
            if (_cache.TryGetValue(dedupeKey, out _))
                return OkResponse(new { streamId = (string?)null, status = "already_counted" });
            _cache.Set(dedupeKey, true, TimeSpan.FromHours(1));
        }

        // Audio availability is checked when the client actually streams via
        // GET /stream/{trackId}/audio. Verifying here was redundant and caused
        // 500s when storage was temporarily unreachable (B-03).
        var session = await _streams.StartAsync(parsedTrackId, userId);
        return OkResponse(new { streamId = session.Id.ToString(), status = "started" });
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

    public class StreamStartRequest
    {
        public string? TrackId { get; set; }
        public string? Title { get; set; }
    }

    public class StreamStopRequest
    {
        public string? StreamId { get; set; }
    }

    [Authorize]
    [HttpPost("stop")]
    public async Task<IActionResult> Stop([FromBody] StreamStopRequest? body = null, [FromQuery] string? streamId = null)
    {
        var rawStreamId = body?.StreamId ?? streamId;
        if (string.IsNullOrWhiteSpace(rawStreamId) || !Guid.TryParse(rawStreamId, out var sid))
            return ErrorResponse("streamId must be a valid GUID.");

        var session = await _streams.GetByIdAsync(sid);
        if (session is null)
            return NotFoundResponse("Stream session not found.");

        // Ownership check: only the session owner or an admin may stop a stream.
        // Return 404 rather than 403 to avoid leaking session existence to other users.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (session.UserId != userId && !User.IsInRole("Admin"))
            return NotFoundResponse("Stream session not found.");

        await _streams.StopAsync(sid);

        return MessageResponse("Stream stopped.");
    }
}
