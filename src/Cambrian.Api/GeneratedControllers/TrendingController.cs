using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class TrendingController : ControllerBase
{

        [HttpGet("trending")]
        public IActionResult GET_trending()
        {
            return Ok("stub");
        }
}
