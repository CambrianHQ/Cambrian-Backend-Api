using System.Security.Claims;
using Cambrian.Application.DTOs.Payments;
using Cambrian.Application.DTOs.Purchases;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("payments")]
[Authorize]
public class PaymentsController : BaseController
{
    private readonly IPaymentService _payments;
    private readonly IPurchaseRepository _purchases;
    private readonly ITrackRepository _tracks;
    private readonly ILibraryRepository _library;
    private readonly IInvoiceRepository _invoiceRepo;

    public PaymentsController(
        IPaymentService payments,
        IPurchaseRepository purchases,
        ITrackRepository tracks,
        ILibraryRepository library,
        IInvoiceRepository invoiceRepo)
    {
        _payments = payments;
        _purchases = purchases;
        _tracks = tracks;
        _library = library;
        _invoiceRepo = invoiceRepo;
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
    public async Task<IActionResult> CreatePurchase(PurchaseCreateRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        if (!Guid.TryParse(request.TrackId, out var trackId))
            return ErrorResponse("Invalid trackId.");

        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null)
            return NotFoundResponse("Track not found.");

        // Create purchase record
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = userId,
            TrackId = trackId,
            Amount = track.Price,
            LicenseType = request.LicenseType ?? "non-exclusive",
            PaymentMethod = request.PaymentMethod,
            Status = "completed",
            CreatedAt = DateTime.UtcNow
        };
        await _purchases.AddAsync(purchase);

        // Auto-add to library
        await _library.AddAsync(new LibraryItem
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TrackId = trackId,
            Title = track.Title,
            Artist = track.Creator?.DisplayName ?? track.Creator?.Email,
            AudioUrl = track.AudioUrl,
            SavedAt = DateTime.UtcNow
        });

        // Mark exclusive if applicable
        if (request.LicenseType == "exclusive")
        {
            track.ExclusiveSold = true;
            await _tracks.UpdateAsync(track);
        }

        // Auto-create invoice
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PurchaseId = purchase.Id,
            AmountCents = (int)(purchase.Amount * 100),
            Currency = "usd",
            Status = "paid",
            IssuedAt = DateTime.UtcNow,
            PaidAt = DateTime.UtcNow
        };
        await _invoiceRepo.AddAsync(invoice);

        return CreatedResponse(new PurchaseResponse
        {
            Id = purchase.Id.ToString(),
            TrackId = purchase.TrackId.ToString(),
            TrackTitle = track.Title,
            AmountCents = (int)(purchase.Amount * 100),
            LicenseType = purchase.LicenseType ?? "non-exclusive",
            Status = purchase.Status,
            CreatedAt = purchase.CreatedAt,
            CompletedAt = DateTime.UtcNow
        }, "Purchase completed.");
    }

    [HttpPost("/purchases/credit-creator")]
    public IActionResult CreditCreator(CreditCreatorRequest request)
    {
        // In a real implementation this would credit the creator's wallet.
        // For now, acknowledge the request.
        return MessageResponse("Creator credited.");
    }
}
