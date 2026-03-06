using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class CheckoutController : ControllerBase
{
    private readonly ICheckoutService _checkout;

    public CheckoutController(ICheckoutService checkout)
    {
        _checkout = checkout;
    }

    [Authorize]
    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout(CheckoutRequest request)
    {
        var session = await _checkout.CreateCheckoutAsync(request, User);
        return Ok(session);
    }
}