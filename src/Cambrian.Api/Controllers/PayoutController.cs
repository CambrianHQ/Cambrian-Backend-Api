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
    private readonly IPayoutRepository _payoutRepo;

    public PayoutController(IPayoutService payouts, IPayoutRepository payoutRepo)
    {
        _payouts = payouts;
        _payoutRepo = payoutRepo;
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
    public async Task<IActionResult> PayoutsEarnings()
    {
        var earnings = await _payouts.GetEarningsAsync();
        return OkResponse(earnings);
    }

    [HttpPost("request")]
    public async Task<IActionResult> RequestPayout(PayoutRequest request)
    {
        var result = await _payouts.RequestAsync(request);
        return OkResponse(result);
    }

    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] int take = 50)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var payouts = await _payoutRepo.GetByCreatorIdAsync(userId);

        var history = payouts.Take(take).Select(p => new
        {
            id = p.Id.ToString(),
            amount = (decimal)p.Amount,
            status = p.Status,
            requestedAt = p.RequestedAt,
            completedAt = p.CompletedAt
        }).ToList();

        return OkResponse(history);
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
    public async Task<IActionResult> Earnings()
    {
        var earnings = await _payouts.GetEarningsAsync();
        return OkResponse(earnings);
    }
}
