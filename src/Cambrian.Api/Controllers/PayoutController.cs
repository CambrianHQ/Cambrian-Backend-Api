using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.Payouts;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cambrian.Api.Controllers;

// [RequireCreatorTier] removed from class level (issue #73). It is re-applied
// per-method to every action EXCEPT the two earnings endpoints, so an account
// with Role = "User" can read its earnings dashboard. Money-moving actions
// (request, connect, disconnect, dashboard) still require Creator role.
[Route("payouts")]
[Authorize]
[RequireUsername]
[EnableRateLimiting("auth")]
public class PayoutController : BaseController
{
    private readonly IPayoutService _payouts;
    private readonly ICreatorConnectService _connect;

    public PayoutController(IPayoutService payouts, ICreatorConnectService connect)
    {
        _payouts = payouts;
        _connect = connect;
    }

    [RequireCreatorTier]
    [HttpPost("connect-stripe")]
    public async Task<IActionResult> ConnectStripe()
    {
        var userId = GetRequiredUserId()!;
        var result = await _connect.StartOnboardingAsync(userId);
        return OkResponse(result);
    }

    [RequireCreatorTier]
    [HttpGet("connect-status")]
    public async Task<IActionResult> ConnectStatus()
    {
        var userId = GetRequiredUserId()!;
        var status = await _connect.GetStatusAsync(userId);
        return OkResponse(status);
    }

    [RequireCreatorTier]
    [HttpGet("stripe-dashboard")]
    public async Task<IActionResult> StripeDashboard()
    {
        var userId = GetRequiredUserId()!;
        var url = await _connect.GetDashboardLinkAsync(userId);
        return OkResponse(new { url });
    }

    [RequireCreatorTier]
    [HttpGet("account")]
    public async Task<IActionResult> Account()
    {
        var userId = GetRequiredUserId()!;
        var status = await _connect.GetStatusAsync(userId);
        return OkResponse(new { connected = status.Connected, status = status.Status });
    }

    [RequireCreatorTier]
    [HttpPost("connect")]
    public async Task<IActionResult> Connect()
    {
        var userId = GetRequiredUserId()!;
        var result = await _connect.StartOnboardingAsync(userId);
        return OkResponse(result);
    }

    [RequireCreatorTier]
    [HttpDelete("disconnect")]
    public async Task<IActionResult> DisconnectDelete()
    {
        var userId = GetRequiredUserId()!;
        await _connect.DisconnectAsync(userId);
        return MessageResponse("Stripe account disconnected.");
    }

    [RequireCreatorTier]
    [HttpPost("disconnect")]
    public Task<IActionResult> DisconnectPost() => DisconnectDelete();

    // No [RequireCreatorTier] — issue #73 carve-out.
    [HttpGet("earnings")]
    public async Task<IActionResult> PayoutsEarnings()
    {
        var userId = GetRequiredUserId()!;
        var earnings = await _payouts.GetEarningsAsync(userId);
        return OkResponse(earnings);
    }

    [RequireCreatorTier]
    [Authorize(Policy = "VerifiedEmail")]
    [HttpPost("request")]
    public async Task<IActionResult> RequestPayout(PayoutRequest request)
    {
        var userId = GetRequiredUserId()!;
        var result = await _payouts.RequestAsync(request, userId);
        return OkResponse(result);
    }

    [RequireCreatorTier]
    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] int take = 50)
    {
        var userId = GetRequiredUserId()!;
        var history = await _payouts.GetHistoryAsync(userId, take);
        return OkResponse(history);
    }

    [RequireCreatorTier]
    [HttpPost("settings")]
    public IActionResult CreateSettings([FromBody] PayoutSettingsRequest? request = null)
    {
        return MessageResponse("Payout settings saved.");
    }

    [RequireCreatorTier]
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

    // No [RequireCreatorTier] — alias for the carve-out endpoint above.
    [HttpGet("/earnings")]
    public Task<IActionResult> Earnings() => PayoutsEarnings();
}
