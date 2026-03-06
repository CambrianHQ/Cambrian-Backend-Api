using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class DiscoverController : ControllerBase
{

        [HttpGet("discover")]
        public IActionResult GET_discover()
        {
            return Ok("stub");
        }
}
