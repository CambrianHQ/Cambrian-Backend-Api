using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class AiController : ControllerBase
{

        [HttpGet("ai/playlist")]
        public IActionResult GET_ai_playlist()
        {
            return Ok("stub");
        }
}
