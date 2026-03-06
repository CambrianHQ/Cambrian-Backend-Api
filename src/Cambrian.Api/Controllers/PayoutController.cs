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

    [HttpGet("earnings")]
    public async Task<IActionResult> Earnings()
    {
        return OkResponse(await _payouts.GetEarningsAsync());
    }

    [HttpPost("request")]
    public async Task<IActionResult> RequestPayout(PayoutRequest req)
    {
        var result = await _payouts.RequestAsync(req);
        return OkResponse(result);
    }
}