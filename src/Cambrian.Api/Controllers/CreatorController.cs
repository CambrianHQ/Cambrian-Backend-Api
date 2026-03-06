using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("creator")]
[Authorize(Roles = "Creator")]
public class CreatorController : BaseController
{
    [HttpGet("tracks")]
    public IActionResult Tracks([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 50;
        return OkResponse(Array.Empty<object>());
    }

    [HttpGet("revenue")]
    public IActionResult Revenue()
    {
        return OkResponse(new { pendingBalance = 0m, availableBalance = 0m });
    }
}
