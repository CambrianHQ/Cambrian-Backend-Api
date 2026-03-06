using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class UploadController : ControllerBase
{

        [HttpPost("upload")]
        public IActionResult POST_upload()
        {
            return Ok("stub");
        }
}
