using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("stream")]
public class StreamController : BaseController
{
    [HttpGet]
    public IActionResult List([FromQuery] int take = 20)
    {
        return OkResponse(Array.Empty<object>());
    }

    [HttpGet("{trackId}")]
    public IActionResult Stream(string trackId)
    {
        if (!Guid.TryParse(trackId, out _))
            return ErrorResponse("trackId must be a valid GUID.");

        return OkResponse(new { trackId, streamUrl = (string?)null });
    }

    [Authorize]
    [HttpPost("start")]
    public IActionResult Start()
    {
        return OkResponse(new { streamId = Guid.NewGuid().ToString(), status = "started" });
    }

    [Authorize]
    [HttpPost("stop")]
    public IActionResult Stop()
    {
        return MessageResponse("Stream stopped.");
    }
}
