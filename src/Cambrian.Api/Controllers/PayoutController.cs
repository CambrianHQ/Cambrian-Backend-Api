using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("payouts")]
[Authorize]
public class PayoutController : BaseController
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
    public IActionResult DisconnectDelete()
    {
        return MessageResponse("Stripe account disconnected.");
    }

    [HttpPost("disconnect")]
    public IActionResult DisconnectPost()
    {
        return MessageResponse("Stripe account disconnected.");
    }

    [HttpGet("earnings")]
    public IActionResult PayoutsEarnings()
    {
        return OkResponse(new { total = 0m, pending = 0m, available = 0m });
    }

    [HttpPost("request")]
    public IActionResult RequestPayout()
    {
        return OkResponse(new { amount = 0m, status = "pending" });
    }

    [HttpGet("history")]
    public IActionResult History([FromQuery] int take = 50)
    {
        return OkResponse(Array.Empty<object>());
    }

    [HttpPost("settings")]
    public IActionResult CreateSettings()
    {
        return MessageResponse("Payout settings saved.");
    }

    [HttpPut("settings")]
    public IActionResult UpdateSettings()
    {
        return MessageResponse("Payout settings updated.");
    }

    /// <summary>GET /earnings — root-level alias for payout earnings.</summary>
    [HttpGet("/earnings")]
    public IActionResult Earnings()
    {
        return OkResponse(new { total = 0m, pending = 0m, available = 0m });
    }
}
