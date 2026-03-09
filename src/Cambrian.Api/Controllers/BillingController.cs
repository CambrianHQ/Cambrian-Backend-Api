using System.Security.Claims;
using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Cambrian.Api.Controllers;

[Route("billing")]
[Authorize]
public class BillingController : BaseController
{
    private readonly IBillingService _billing;
    private readonly string _frontendUrl;

    public BillingController(IBillingService billing, IConfiguration configuration)
    {
        _billing = billing;
        _frontendUrl = configuration["App:FrontendUrl"] ?? "http://localhost:5173";
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout(BillingCheckoutRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        try
        {
            var result = await _billing.CreateCheckoutAsync(request.Tier ?? "", userId, _frontendUrl);
            return OkResponse(new { url = result.CheckoutUrl });
        }
        catch (ArgumentException ex)
        {
            return ErrorResponse(ex.Message);
        }
    }

    [HttpPost("checkout-session")]
    public async Task<IActionResult> CheckoutSession(BillingCheckoutRequest request)
    {
        return await Checkout(request);
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var status = await _billing.GetStatusAsync(userId);
        return OkResponse(new { tier = status.Tier, status = status.Status });
    }

    [HttpGet("checkout-session/{sessionId}")]
    public IActionResult GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return ErrorResponse("sessionId is required.");
        return OkResponse(new { sessionId });
    }
}
