using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("stream")]
public class StreamController : BaseController
{
    private readonly IStreamService _stream;

    public StreamController(IStreamService stream)
    {
        _stream = stream;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int take = 20)
    {
        var result = await _stream.ListStreamableAsync(take);
        return OkResponse(result);
    }

    [HttpGet("{trackId}")]
    public async Task<IActionResult> Stream(string trackId)
    {
        var result = await _stream.GetStreamUrlAsync(trackId);
        return OkResponse(result);
    }

    [Authorize]
    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StreamStartRequest? body = null, [FromQuery] string? trackId = null)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var rawTrackId = body?.TrackId ?? trackId;
        var result = await _stream.StartAsync(rawTrackId, userId);
        return OkResponse(result);
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
        await _stream.StopAsync(streamId);
        return MessageResponse("Stream stopped.");
    }
}
