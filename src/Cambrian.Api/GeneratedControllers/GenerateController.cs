using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class GenerateController : ControllerBase
{

        [HttpPost("generate")]
        public IActionResult POST_generate()
        {
            return Ok("stub");
        }
}
