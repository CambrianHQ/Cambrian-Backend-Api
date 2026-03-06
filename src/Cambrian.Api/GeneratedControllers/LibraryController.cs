using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class LibraryController : ControllerBase
{

        [HttpGet("library")]
        public IActionResult GET_library()
        {
            return Ok("stub");
        }

        [HttpPost("library")]
        public IActionResult POST_library()
        {
            return Ok("stub");
        }

        [HttpGet("library/purchased-track-ids")]
        public IActionResult GET_library_purchased_track_ids()
        {
            return Ok("stub");
        }

        [HttpDelete("library/{trackId}")]
        public IActionResult DELETE_library_trackId()
        {
            return Ok("stub");
        }

        [HttpPost("library/{trackId}")]
        public IActionResult POST_library_trackId()
        {
            return Ok("stub");
        }
}
