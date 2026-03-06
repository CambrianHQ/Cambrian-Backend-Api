using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class DownloadController : ControllerBase
{

        [HttpGet("download/{trackId}")]
        public IActionResult GET_download_trackId()
        {
            return Ok("stub");
        }

        [HttpGet("download/{trackId}/signed")]
        public IActionResult GET_download_trackId_signed()
        {
            return Ok("stub");
        }
}
