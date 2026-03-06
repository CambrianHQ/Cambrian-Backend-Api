using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class HealthController : ControllerBase
{

        [HttpGet("health")]
        public IActionResult GET_health()
        {
            return Ok("stub");
        }
}
