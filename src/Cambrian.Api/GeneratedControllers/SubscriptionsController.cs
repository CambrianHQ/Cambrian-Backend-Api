using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class SubscriptionsController : ControllerBase
{

        [HttpGet("subscriptions/plans")]
        public IActionResult GET_subscriptions_plans()
        {
            return Ok("stub");
        }

        [HttpGet("subscriptions/current")]
        public IActionResult GET_subscriptions_current()
        {
            return Ok("stub");
        }

        [HttpPost("subscriptions/update")]
        public IActionResult POST_subscriptions_update()
        {
            return Ok("stub");
        }
}
