using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("ai")]
[Authorize]
public class AiController : BaseController
{
    [HttpGet("playlist")]
    public IActionResult GetPlaylist()
    {
        return OkResponse(Array.Empty<object>());
    }
}
