using Cambrian.Application.DTOs.Payments;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("payments")]
[Authorize]
public class PaymentsController : BaseController
{
    private readonly IPaymentService _payments;

    public PaymentsController(IPaymentService payments)
    {
        _payments = payments;
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout(PaymentCheckoutRequest request)
    {
        var result = await _payments.CreateCheckoutAsync(request);
        return OkResponse(result);
    }

    [HttpGet("state")]
    public async Task<IActionResult> State()
    {
        return OkResponse(await _payments.GetStateAsync());
    }

    [AllowAnonymous]
    [HttpGet("result")]
    public async Task<IActionResult> Result([FromQuery] string? status, [FromQuery] string? trackId)
    {
        return OkResponse(await _payments.GetResultAsync(status, trackId));
    }

    [HttpPost("process")]
    public async Task<IActionResult> Process(PaymentProcessRequest request)
    {
        await _payments.ProcessAsync(request);
        return MessageResponse("Payment processed.");
    }

    // --- Purchases (merged from /purchases/* OpenAPI routes) ---

    [HttpPost("/purchases")]
    public IActionResult CreatePurchase()
    {
        return CreatedResponse<object?>(null, "Purchase initiated.");
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("/purchases/credit-creator")]
    public IActionResult CreditCreator()
    {
        return MessageResponse("Creator credited.");
    }
}
