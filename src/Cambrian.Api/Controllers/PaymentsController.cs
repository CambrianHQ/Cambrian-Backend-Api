using Cambrian.Application.DTOs.Payments;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("payments")]
public class PaymentsController : ControllerBase
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
        return Ok(result);
    }

    [HttpGet("state")]
    public async Task<IActionResult> State()
    {
        var result = await _payments.GetStateAsync();
        return Ok(result);
    }

    [HttpGet("result")]
    public async Task<IActionResult> Result([FromQuery] string? status, [FromQuery] string? trackId)
    {
        var result = await _payments.GetResultAsync(status, trackId);
        return Ok(result);
    }

    [HttpPost("process")]
    public async Task<IActionResult> Process(PaymentProcessRequest request)
    {
        await _payments.ProcessAsync(request);
        return Ok();
    }
}
