using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("stream")]
public class StreamController : BaseController
{
    private readonly ITrackRepository _tracks;
    private readonly IObjectStorage _storage;
    private readonly IStreamRepository _streams;
    private readonly ITrackVisibilityPolicy _visibility;
    private readonly ILogger<StreamController> _logger;

    public StreamController(ITrackRepository tracks, IObjectStorage storage, IStreamRepository streams, ITrackVisibilityPolicy visibility, ILogger<StreamController> logger)
    {
        _tracks = tracks;
        _storage = storage;
        _streams = streams;
        _visibility = visibility;
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

        if (string.IsNullOrEmpty(track.AudioUrl))
            return ErrorResponse("Track has no audio file configured.");

        // C4: enforce visibility via shared policy — single source of truth.
        var streamUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!_visibility.CanAccess(track.Visibility, track.CreatorId, streamUserId, User.IsInRole("Admin")))
            return NotFoundResponse("Track not found.");

        var streamUrl = _storage.GenerateSignedUrl(track.AudioUrl);
        return OkResponse(new { trackId, streamUrl = ResolveAbsoluteUrl(streamUrl) });
    }

    /// <summary>
    /// Redirects to a pre-signed storage URL for the track audio.
    /// The CDN (R2/S3) handles Range requests natively, giving Safari/iOS
    /// proper 206 Partial Content without any server-side buffering.
    /// Open to anonymous users — this is the marketplace discovery/preview model.
    /// Full-quality downloads require a verified purchase (see DownloadController).
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

        if (string.IsNullOrEmpty(track.AudioUrl))
            return ErrorResponse("Track has no audio file configured.");

        // C4: enforce visibility via shared policy (anonymous users allowed for public tracks).
        var audioUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!_visibility.CanAccess(track.Visibility, track.CreatorId, audioUserId, User.IsInRole("Admin")))
            return NotFoundResponse("Track not found.");

        _logger.LogInformation("StreamAudio: streaming trackId={TrackId} via backend proxy", trackId);

        // Always proxy audio through the backend to avoid CORS issues with R2/S3.
        // The browser's <audio> element follows redirects but cross-origin R2 URLs
        // lack CORS headers, causing playback to fail silently.
        var file = await _storage.OpenReadAsync(track.AudioUrl);
        if (file is null)
            return NotFoundResponse("Audio file not found on storage.");

        return File(file.Stream, file.ContentType, enableRangeProcessing: true);
    }

    [Authorize]
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

        // C4: enforce visibility via shared policy.
        if (!_visibility.CanAccess(track.Visibility, track.CreatorId, userId, User.IsInRole("Admin")))
            return NotFoundResponse("Track not found.");

        if (string.IsNullOrEmpty(track.AudioUrl))
            return ErrorResponse("Track has no audio file configured.");

        var session = await _streams.StartAsync(parsedTrackId, userId);
        return OkResponse(new { streamId = session.Id.ToString(), status = "started" });
    }

    public class StreamStartRequest
    {
        public string? TrackId { get; set; }
        public string? Title { get; set; }
    }

    [Authorize]
    [HttpPost("stop")]
    public async Task<IActionResult> Stop([FromQuery] string? streamId = null)
    {
        if (string.IsNullOrWhiteSpace(streamId) || !Guid.TryParse(streamId, out var sid))
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
