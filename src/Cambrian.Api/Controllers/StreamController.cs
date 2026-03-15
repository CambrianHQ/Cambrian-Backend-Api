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
    private readonly ILogger<StreamController> _logger;

    public StreamController(ITrackRepository tracks, IObjectStorage storage, IStreamRepository streams, ILogger<StreamController> logger)
    {
        _tracks = tracks;
        _storage = storage;
        _streams = streams;
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
                artist = t.Creator?.DisplayName ?? t.Creator?.Email ?? "Unknown",
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
        if (track?.AudioUrl is null)
            return NotFoundResponse("Track not found.");

        var streamUrl = _storage.GenerateSignedUrl(track.AudioUrl);
        return OkResponse(new { trackId, streamUrl = ResolveAbsoluteUrl(streamUrl) });
    }

    /// <summary>
    /// Proxy-streams the actual audio bytes for a track.
    /// This endpoint is what the &lt;audio&gt; element should point at.
    /// Supports HTTP Range requests for seeking / Safari compatibility.
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
        if (track?.AudioUrl is null)
            return NotFoundResponse("Track not found.");

        _logger.LogInformation("StreamAudio: trackId={TrackId}, dbAudioUrl={AudioUrl}", trackId, track.AudioUrl);

        StorageFile? file;
        try
        {
            file = await _storage.OpenReadAsync(track.AudioUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamAudio: Storage error for key={AudioUrl}. Returning silent placeholder.", track.AudioUrl);
            var silentFallback = Cambrian.Api.Tools.SilentMp3Generator.Generate();
            return File(silentFallback, "audio/mpeg", enableRangeProcessing: true);
        }

        if (file is null)
        {
            _logger.LogWarning("StreamAudio: File not found in storage. key={AudioUrl}. Returning silent placeholder.", track.AudioUrl);
            // Return a generated silent MP3 so the player never breaks
            var silent = Cambrian.Api.Tools.SilentMp3Generator.Generate();
            return File(silent, "audio/mpeg", enableRangeProcessing: true);
        }

        // Let ASP.NET Core handle Range requests (required by Safari / iOS)
        Response.Headers["Accept-Ranges"] = "bytes";
        Response.Headers["Cache-Control"] = "private, max-age=3600";

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
        if (track?.AudioUrl is null)
            return NotFoundResponse("Track not found.");

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

        await _streams.StopAsync(sid);

        return MessageResponse("Stream stopped.");
    }
}
