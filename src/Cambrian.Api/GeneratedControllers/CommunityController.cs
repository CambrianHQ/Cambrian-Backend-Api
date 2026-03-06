using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class CommunityController : ControllerBase
{

        [HttpGet("community")]
        public IActionResult GET_community()
        {
            return Ok("stub");
        }
}
