using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("payouts")]
[Authorize]
public class PayoutsController : BaseController
{
    [HttpPost("connect-stripe")]
    public IActionResult ConnectStripe()
    {
        return OkResponse(new { connectUrl = (string?)null });
    }

    [HttpGet("connect-status")]
    public IActionResult ConnectStatus()
    {
        return OkResponse(new { connected = false });
    }

    [HttpGet("stripe-dashboard")]
    public IActionResult StripeDashboard()
    {
        return OkResponse(new { url = (string?)null });
    }

    [HttpGet("account")]
    public IActionResult Account()
    {
        return OkResponse(new { accountId = (string?)null, status = "not_connected" });
    }

    [HttpPost("connect")]
    public IActionResult Connect()
    {
        return OkResponse(new { connectUrl = (string?)null });
    }

    [HttpDelete("disconnect")]
    public IActionResult Disconnect()
    {
        return MessageResponse("Stripe account disconnected.");
    }

    [HttpGet("history")]
    public IActionResult History([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;
        return OkResponse(Array.Empty<object>());
    }

    [HttpPut("settings")]
    public IActionResult UpdateSettings()
    {
        return MessageResponse("Payout settings updated.");
    }
}
