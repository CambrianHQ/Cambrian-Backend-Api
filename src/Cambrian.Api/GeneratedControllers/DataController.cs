using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class DataController : ControllerBase
{

        [HttpGet("data/account")]
        public IActionResult GET_data_account()
        {
            return Ok("stub");
        }

        [HttpGet("data/songs")]
        public IActionResult GET_data_songs()
        {
            return Ok("stub");
        }

        [HttpPost("data/songs")]
        public IActionResult POST_data_songs()
        {
            return Ok("stub");
        }

        [HttpGet("data/system")]
        public IActionResult GET_data_system()
        {
            return Ok("stub");
        }

        [HttpPost("data/system")]
        public IActionResult POST_data_system()
        {
            return Ok("stub");
        }

        [HttpGet("data/secrets")]
        public IActionResult GET_data_secrets()
        {
            return Ok("stub");
        }

        [HttpPost("data/secrets")]
        public IActionResult POST_data_secrets()
        {
            return Ok("stub");
        }
}
