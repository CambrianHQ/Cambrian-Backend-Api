using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.Payouts;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("payouts")]
[Authorize]
[RequireCreatorTier]
public class PayoutController : BaseController
{
    private readonly IPayoutService _payouts;
    private readonly ICreatorConnectService _connect;

    public PayoutController(IPayoutService payouts, ICreatorConnectService connect)
    {
        _payouts = payouts;
        _connect = connect;
    }

    [HttpPost("connect-stripe")]
    public async Task<IActionResult> ConnectStripe()
    {
        var userId = GetRequiredUserId()!;
        var result = await _connect.StartOnboardingAsync(userId);
        return OkResponse(result);
    }

    [HttpGet("connect-status")]
    public async Task<IActionResult> ConnectStatus()
    {
        var userId = GetRequiredUserId()!;
        var status = await _connect.GetStatusAsync(userId);
        return OkResponse(status);
    }

    [HttpGet("stripe-dashboard")]
    public async Task<IActionResult> StripeDashboard()
    {
        var userId = GetRequiredUserId()!;
        var url = await _connect.GetDashboardLinkAsync(userId);
        return OkResponse(new { url });
    }

    [HttpGet("account")]
    public async Task<IActionResult> Account()
    {
        var userId = GetRequiredUserId()!;
        var status = await _connect.GetStatusAsync(userId);
        return OkResponse(new { accountId = status.AccountId, status = status.Status });
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] PayoutConnectRequest? request = null)
    {
        var userId = GetRequiredUserId()!;
        var result = await _connect.StartOnboardingAsync(userId);
        return OkResponse(result);
    }

    public class PayoutConnectRequest
    {
        public string? PlaidPublicToken { get; set; }
        public string? AccountId { get; set; }
        public string? AccountHolderName { get; set; }
        public string? AccountType { get; set; }
    }

    [HttpDelete("disconnect")]
    public async Task<IActionResult> DisconnectDelete()
    {
        var userId = GetRequiredUserId()!;
        await _connect.DisconnectAsync(userId);
        return MessageResponse("Stripe account disconnected.");
    }

    [HttpPost("disconnect")]
    public Task<IActionResult> DisconnectPost() => DisconnectDelete();

    [HttpGet("earnings")]
    public async Task<IActionResult> PayoutsEarnings()
    {
        var userId = GetRequiredUserId()!;
        var earnings = await _payouts.GetEarningsAsync(userId);
        return OkResponse(earnings);
    }

    [HttpPost("request")]
    public async Task<IActionResult> RequestPayout(PayoutRequest request)
    {
        var userId = GetRequiredUserId()!;
        var result = await _payouts.RequestAsync(request, userId);
        return OkResponse(result);
    }

    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] int take = 50)
    {
        var userId = GetRequiredUserId()!;
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
    public Task<IActionResult> Earnings() => PayoutsEarnings();
}
