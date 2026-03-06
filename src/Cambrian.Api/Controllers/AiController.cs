using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("ai")]
[Authorize]
public class AiController : BaseController
{
    [HttpGet("playlist")]
    public IActionResult GetPlaylist([FromQuery] string? seedTrackId = null)
    {
        return OkResponse(Array.Empty<object>());
    }

    /// <summary>POST /generate — AI track generation.</summary>
    [HttpPost("/generate")]
    public IActionResult Generate()
    {
        return OkResponse(new { message = "Generation initiated." });
    }
}
