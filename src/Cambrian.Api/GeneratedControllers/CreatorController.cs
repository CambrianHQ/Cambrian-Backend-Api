using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class CreatorController : ControllerBase
{

        [HttpGet("creator/tracks")]
        public IActionResult GET_creator_tracks()
        {
            return Ok("stub");
        }

        [HttpGet("creator/revenue")]
        public IActionResult GET_creator_revenue()
        {
            return Ok("stub");
        }
}
