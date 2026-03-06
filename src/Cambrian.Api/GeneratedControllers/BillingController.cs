using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class BillingController : ControllerBase
{

        [HttpPost("billing/checkout")]
        public IActionResult POST_billing_checkout()
        {
            return Ok("stub");
        }

        [HttpGet("billing/status")]
        public IActionResult GET_billing_status()
        {
            return Ok("stub");
        }

        [HttpGet("billing/checkout-session/{sessionId}")]
        public IActionResult GET_billing_checkout_session_sessionId()
        {
            return Ok("stub");
        }
}
