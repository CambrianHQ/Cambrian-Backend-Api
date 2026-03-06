using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("health")]
public class HealthController : BaseController
{
    [HttpGet]
    public IActionResult Get()
    {
        return OkResponse(new { status = "ok", timestamp = DateTime.UtcNow });
    }
}
