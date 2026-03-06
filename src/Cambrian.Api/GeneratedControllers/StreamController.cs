using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class StreamController : ControllerBase
{

        [HttpGet("stream")]
        public IActionResult GET_stream()
        {
            return Ok("stub");
        }

        [HttpGet("stream/{trackId}")]
        public IActionResult GET_stream_trackId()
        {
            return Ok("stub");
        }

        [HttpPost("stream/start")]
        public IActionResult POST_stream_start()
        {
            return Ok("stub");
        }

        [HttpPost("stream/stop")]
        public IActionResult POST_stream_stop()
        {
            return Ok("stub");
        }
}
