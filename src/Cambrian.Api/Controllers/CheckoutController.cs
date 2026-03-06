using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("checkout")]
public class CheckoutController : ControllerBase
{
    private readonly ICheckoutService _checkout;

    public CheckoutController(ICheckoutService checkout)
    {
        _checkout = checkout;
    }

    [HttpPost]
    public async Task<IActionResult> Create(CheckoutRequest req)
    {
        var result = await _checkout.CreateCheckoutAsync(req);
        return Ok(result);
    }
}