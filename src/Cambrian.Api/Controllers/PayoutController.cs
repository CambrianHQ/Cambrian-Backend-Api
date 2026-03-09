using System.Security.Claims;
using Cambrian.Application.DTOs.Payouts;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("payouts")]
[Authorize]
public class PayoutController : BaseController
{
    private readonly IPayoutService _payouts;

    public PayoutController(IPayoutService payouts)
    {
        _payouts = payouts;
    }

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
    public IActionResult Connect([FromBody] PayoutConnectRequest? request = null)
    {
        return OkResponse(new { connectUrl = (string?)null, status = "pending" });
    }

    public class PayoutConnectRequest
    {
        public string? PlaidPublicToken { get; set; }
        public string? AccountId { get; set; }
        public string? AccountHolderName { get; set; }
        public string? AccountType { get; set; }
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
    public async Task<IActionResult> PayoutsEarnings()
    {
        var earnings = await _payouts.GetEarningsAsync();
        return OkResponse(earnings);
    }

    [HttpPost("request")]
    public async Task<IActionResult> RequestPayout(PayoutRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await _payouts.RequestAsync(request, userId);
        return OkResponse(result);
    }

    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] int take = 50)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var history = await _payouts.GetHistoryAsync(userId, take);
        return OkResponse(history);
    }

    [HttpPost("settings")]
    public IActionResult CreateSettings([FromBody] PayoutSettingsRequest? request = null)
    {
        return MessageResponse("Payout settings saved.");
    }

    [HttpPut("settings")]
    public IActionResult UpdateSettings([FromBody] PayoutSettingsRequest? request = null)
    {
        return MessageResponse("Payout settings updated.");
    }

    public class PayoutSettingsRequest
    {
        public decimal? Threshold { get; set; }
        public string? Schedule { get; set; }
    }

    [HttpGet("/earnings")]
    public async Task<IActionResult> Earnings()
    {
        var earnings = await _payouts.GetEarningsAsync();
        return OkResponse(earnings);
    }
}
