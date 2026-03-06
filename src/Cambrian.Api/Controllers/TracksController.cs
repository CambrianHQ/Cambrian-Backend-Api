using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("tracks")]
public class TracksController : BaseController
{
    [HttpGet]
    public IActionResult List([FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] string? genre = null, [FromQuery] string? search = null)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 50;
        return OkResponse(Array.Empty<object>());
    }

    [Authorize(Roles = "Creator")]
    [HttpPost("upload")]
    public IActionResult Upload()
    {
        return CreatedResponse<object?>(null, "Track upload initiated.");
    }
}
