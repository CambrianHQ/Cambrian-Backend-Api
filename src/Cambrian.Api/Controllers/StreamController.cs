using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("stream")]
public class StreamController : BaseController
{
    private readonly ITrackRepository _tracks;
    private readonly IObjectStorage _storage;
    private readonly IStreamRepository _streams;

    public StreamController(ITrackRepository tracks, IObjectStorage storage, IStreamRepository streams)
    {
        _tracks = tracks;
        _storage = storage;
        _streams = streams;
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int take = 20)
    {
        var tracks = await _tracks.BrowseAsync();
        var result = tracks.Take(take).Select(t => new
        {
            id = t.Id.ToString(),
            title = t.Title,
            artist = t.Creator?.DisplayName ?? t.Creator?.Email ?? "Unknown",
            genre = t.Genre,
            duration = t.Duration,
            audioUrl = ResolveAbsoluteUrl(t.AudioUrl)
        }).ToList();

        return OkResponse(result);
    }

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
