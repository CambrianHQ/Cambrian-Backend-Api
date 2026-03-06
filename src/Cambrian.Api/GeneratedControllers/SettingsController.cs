using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class SettingsController : ControllerBase
{

        [HttpGet("settings/profile")]
        public IActionResult GET_settings_profile()
        {
            return Ok("stub");
        }

        [HttpPost("settings/password")]
        public IActionResult POST_settings_password()
        {
            return Ok("stub");
        }

        [HttpPut("settings/password")]
        public IActionResult PUT_settings_password()
        {
            return Ok("stub");
        }

        [HttpPost("settings/email")]
        public IActionResult POST_settings_email()
        {
            return Ok("stub");
        }

        [HttpPut("settings/email")]
        public IActionResult PUT_settings_email()
        {
            return Ok("stub");
        }
}
