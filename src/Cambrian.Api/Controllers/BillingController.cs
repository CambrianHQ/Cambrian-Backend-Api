using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("billing")]
[Authorize]
public class BillingController : BaseController
{
    [HttpPost("checkout")]
    public IActionResult Checkout()
    {
        return OkResponse(new { checkoutUrl = (string?)null });
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        return OkResponse(new { tier = "free", status = "active" });
    }

    [HttpGet("checkout-session/{sessionId}")]
    public IActionResult GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return ErrorResponse("sessionId is required.");
        return OkResponse(new { sessionId });
    }
}
