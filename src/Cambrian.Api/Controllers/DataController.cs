using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("data")]
[Authorize(Roles = "Admin")]
public class DataController : BaseController
{
    [HttpGet("account")]
    public IActionResult GetAccount()
    {
        return OkResponse(new { });
    }

    [HttpGet("songs")]
    public IActionResult GetSongs()
    {
        return OkResponse(Array.Empty<object>());
    }

    [HttpPost("songs")]
    public IActionResult PostSongs()
    {
        return MessageResponse("Songs data received.");
    }

    [HttpGet("system")]
    public IActionResult GetSystem()
    {
        return OkResponse(new { });
    }

    [HttpPost("system")]
    public IActionResult PostSystem()
    {
        return MessageResponse("System data received.");
    }

    [HttpGet("secrets")]
    public IActionResult GetSecrets()
    {
        return OkResponse(new { });
    }

    [HttpPost("secrets")]
    public IActionResult PostSecrets()
    {
        return MessageResponse("Secrets data received.");
    }
}
