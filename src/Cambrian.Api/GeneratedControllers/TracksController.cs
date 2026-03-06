using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class TracksController : ControllerBase
{

        [HttpGet("tracks/{trackId}")]
        public IActionResult GET_tracks_trackId()
        {
            return Ok("stub");
        }

        [HttpGet("tracks")]
        public IActionResult GET_tracks()
        {
            return Ok("stub");
        }

        [HttpGet("tracks/{id}")]
        public IActionResult GET_tracks_id()
        {
            return Ok("stub");
        }

        [HttpPost("tracks/upload")]
        public IActionResult POST_tracks_upload()
        {
            return Ok("stub");
        }
}
