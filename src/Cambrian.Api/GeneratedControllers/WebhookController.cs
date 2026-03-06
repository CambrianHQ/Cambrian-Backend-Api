using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class WebhookController : ControllerBase
{

        [HttpPost("webhook/stripe")]
        public IActionResult POST_webhook_stripe()
        {
            return Ok("stub");
        }
}
