using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("subscriptions")]
[Authorize]
public class SubscriptionsController : BaseController
{
    [AllowAnonymous]
    [HttpGet("plans")]
    public IActionResult Plans()
    {
        return OkResponse(Array.Empty<object>());
    }

    [HttpGet("current")]
    public IActionResult Current()
    {
        return OkResponse(new { tier = "free", status = "active" });
    }

    [HttpPost("update")]
    public IActionResult Update()
    {
        return MessageResponse("Subscription updated.");
    }
}
