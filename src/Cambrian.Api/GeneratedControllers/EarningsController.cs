using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class EarningsController : ControllerBase
{

        [HttpGet("earnings")]
        public IActionResult GET_earnings()
        {
            return Ok("stub");
        }
}
