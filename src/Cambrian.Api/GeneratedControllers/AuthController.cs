using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class AuthController : ControllerBase
{

        [HttpGet("auth/health")]
        public IActionResult GET_auth_health()
        {
            return Ok("stub");
        }

        [HttpPost("auth/register")]
        public IActionResult POST_auth_register()
        {
            return Ok("stub");
        }

        [HttpPost("auth/login")]
        public IActionResult POST_auth_login()
        {
            return Ok("stub");
        }

        [HttpPost("auth/logout")]
        public IActionResult POST_auth_logout()
        {
            return Ok("stub");
        }

        [HttpGet("auth/me")]
        public IActionResult GET_auth_me()
        {
            return Ok("stub");
        }

        [HttpGet("auth/csrf-token")]
        public IActionResult GET_auth_csrf_token()
        {
            return Ok("stub");
        }

        [HttpPost("auth/forgot-password")]
        public IActionResult POST_auth_forgot_password()
        {
            return Ok("stub");
        }

        [HttpPost("auth/verify-code")]
        public IActionResult POST_auth_verify_code()
        {
            return Ok("stub");
        }

        [HttpPost("auth/reset-password")]
        public IActionResult POST_auth_reset_password()
        {
            return Ok("stub");
        }

        [HttpPost("auth/recover-username")]
        public IActionResult POST_auth_recover_username()
        {
            return Ok("stub");
        }
}
