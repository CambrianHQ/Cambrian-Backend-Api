using System.Security.Claims;
using Cambrian.Application.DTOs.Payments;
using Cambrian.Application.DTOs.Purchases;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("payments")]
[Authorize]
public class PaymentsController : BaseController
{
    private readonly IPaymentService _payments;
    private readonly IPurchaseService _purchaseService;

    public PaymentsController(
        IPaymentService payments,
        IPurchaseService purchaseService)
    {
        _payments = payments;
        _purchaseService = purchaseService;
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout(PaymentCheckoutRequest request)
    {
        var userId = GetRequiredUserId()!;
        var userEmail = User.FindFirstValue(ClaimTypes.Email)
                     ?? User.FindFirstValue("email");
        var result = await _payments.CreateCheckoutAsync(request, userId, userEmail);
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
        // Kept anonymous for Stripe checkout redirect flow, but response is sanitized
        return OkResponse(await _payments.GetResultAsync(status, trackId));
    }

    [HttpPost("process")]
    public async Task<IActionResult> Process(PaymentProcessRequest request)
    {
        var userId = GetRequiredUserId()!;
        await _payments.ProcessAsync(request, userId);
        return MessageResponse("Payment processed.");
    }

    // --- Purchases (merged from /purchases/* OpenAPI routes) ---

    [HttpPost("/purchases")]
    public async Task<IActionResult> CreatePurchase(PurchaseCreateRequest request)
    {
        var userId = GetRequiredUserId()!;
        var result = await _purchaseService.CreateAsync(request, userId);
        return CreatedResponse(result, "Purchase completed.");
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("/purchases/credit-creator")]
    public async Task<IActionResult> CreditCreator(CreditCreatorRequest request)
    {
        await _purchaseService.CreditCreatorAsync(request);
        return MessageResponse("Creator credited.");
    }
}
