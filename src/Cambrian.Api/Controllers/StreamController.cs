using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("stream")]
[Authorize]
public class StreamController : BaseController
{
    private readonly IStreamService _streams;

    public StreamController(IStreamService streams)
    {
        _streams = streams;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int take = 20)
    {
        return OkResponse(await _streams.GetTracksAsync(take));
    }

    [HttpGet("{trackId}")]
    public async Task<IActionResult> Stream(string trackId)
    {
        var result = await _streams.GetStreamAsync(trackId);
        return result is null
            ? NotFoundResponse("Track not found.")
            : OkResponse(result);
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StreamStartRequest? body = null, [FromQuery] string? trackId = null)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var rawTrackId = body?.TrackId ?? trackId;
        return OkResponse(await _streams.StartAsync(rawTrackId, userId));
    }

    public class StreamStartRequest
    {
        public string? TrackId { get; set; }
        public string? Title { get; set; }
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop([FromQuery] string? streamId = null)
    {
        await _streams.StopAsync(streamId);
        return MessageResponse("Stream stopped.");
    }
}
