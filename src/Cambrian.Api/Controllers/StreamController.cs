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

    [HttpGet]
    public IActionResult List([FromQuery] int take = 20)
    {
        return OkResponse(Array.Empty<object>());
    }

    [HttpGet("{trackId}")]
    public async Task<IActionResult> Stream(string trackId)
    {
        if (!Guid.TryParse(trackId, out var id))
            return ErrorResponse("trackId must be a valid GUID.");

        var track = await _tracks.GetByIdAsync(id);
        if (track?.AudioUrl is null)
            return NotFoundResponse("Track not found.");

        var streamUrl = _storage.GenerateSignedUrl(track.AudioUrl);
        return OkResponse(new { trackId, streamUrl });
    }

    [Authorize]
    [HttpPost("start")]
    public async Task<IActionResult> Start([FromQuery] string? trackId = null)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid parsedTrackId = Guid.TryParse(trackId, out var tid) ? tid : Guid.Empty;

        var session = await _streams.StartAsync(parsedTrackId, userId);
        return OkResponse(new { streamId = session.Id.ToString(), status = "started" });
    }

    [Authorize]
    [HttpPost("stop")]
    public async Task<IActionResult> Stop([FromQuery] string? streamId = null)
    {
        if (Guid.TryParse(streamId, out var sid))
            await _streams.StopAsync(sid);

        return MessageResponse("Stream stopped.");
    }
}
