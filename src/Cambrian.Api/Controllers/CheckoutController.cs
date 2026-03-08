using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("")]
public class CheckoutController : BaseController
{
    private readonly ICheckoutService _checkout;

    public CheckoutController(ICheckoutService checkout)
    {
        _checkout = checkout;
    }

    /// <summary>
    /// Create a Stripe Checkout session for a track purchase.
    /// Returns { checkoutUrl } for redirect to Stripe.
    /// </summary>
    [Authorize]
    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout(CheckoutRequest request)
    {
        var session = await _checkout.CreateCheckoutAsync(request, User);
        return OkResponse(new { checkoutUrl = session.CheckoutUrl, status = session.Status });
    }
}