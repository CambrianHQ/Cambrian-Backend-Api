using Cambrian.Application.DTOs.Payments;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

public class PaymentService : IPaymentService
{
    private readonly IPaymentGateway _gateway;
    private readonly ITrackRepository _tracks;
    private readonly IPurchaseRepository _purchases;
    private readonly ILibraryRepository _library;
    private readonly IInvoiceRepository _invoices;

    public PaymentService(
        IPaymentGateway gateway,
        ITrackRepository tracks,
        IPurchaseRepository purchases,
        ILibraryRepository library,
        IInvoiceRepository invoices)
    {
        _gateway = gateway;
        _tracks = tracks;
        _purchases = purchases;
        _library = library;
        _invoices = invoices;
    }

    public async Task<PaymentCheckoutResponse> CreateCheckoutAsync(PaymentCheckoutRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TrackId))
            throw new ArgumentException("TrackId is required.");

        var track = await _tracks.GetByIdAsync(Guid.Parse(request.TrackId))
                    ?? throw new KeyNotFoundException($"Track {request.TrackId} not found.");

        var amountCents = (int)(track.Price * 100);

        var url = await _gateway.CreateCheckoutSessionAsync(
            amountCents,
            track.Title,
            clientReferenceId: request.ClientReferenceId ?? request.TrackId);

        return new PaymentCheckoutResponse { CheckoutUrl = url };
    }

    public async Task<PaymentStateResponse> GetStateAsync()
    {
        // Return pending purchases (could be scoped to user in future)
        return await Task.FromResult(new PaymentStateResponse
        {
            Status = "ready",
            PurchaseIds = [],
            ProcessedEventIds = []
        });
    }

    public async Task<PaymentResultResponse> GetResultAsync(string? status, string? trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            return new PaymentResultResponse { Status = status ?? "unknown" };

        var purchases = await _purchases.GetByTrackIdAsync(Guid.Parse(trackId));
        var latest = purchases.FirstOrDefault();

        return new PaymentResultResponse
        {
            Status = latest?.Status ?? status ?? "pending",
            PurchaseId = latest?.Id.ToString(),
            Duplicate = purchases.Count > 1
        };
    }

    public async Task ProcessAsync(PaymentProcessRequest request)
    {
        var purchase = await _purchases.GetByIdAsync(Guid.Parse(request.PurchaseId))
                       ?? throw new KeyNotFoundException($"Purchase {request.PurchaseId} not found.");

        var wasAlreadyCompleted = purchase.Status == "completed";
        purchase.Status = "completed";
        purchase.PaymentMethod = request.PaymentMethodId ?? "stripe";
        await _purchases.UpdateAsync(purchase);

        if (wasAlreadyCompleted) return;

        var track = await _tracks.GetByIdAsync(purchase.TrackId);

        var existingLib = await _library.GetByUserAndTrackAsync(purchase.BuyerId, purchase.TrackId);
        if (existingLib is null)
        {
            await _library.AddAsync(new LibraryItem
            {
                Id = Guid.NewGuid(),
                UserId = purchase.BuyerId,
                TrackId = purchase.TrackId,
                Title = track?.Title ?? "",
                Artist = track?.Creator?.DisplayName ?? "",
                AudioUrl = track?.AudioUrl,
                SavedAt = DateTime.UtcNow
            });
        }

        await _invoices.AddAsync(new Invoice
        {
            Id = Guid.NewGuid(),
            UserId = purchase.BuyerId,
            PurchaseId = purchase.Id,
            AmountCents = (int)(purchase.Amount * 100),
            Currency = "usd",
            Status = "paid",
            IssuedAt = DateTime.UtcNow,
            PaidAt = DateTime.UtcNow
        });

        if (track is not null && purchase.LicenseType == "exclusive" && !track.ExclusiveSold)
        {
            track.ExclusiveSold = true;
            track.Visibility = "hidden";
            await _tracks.UpdateAsync(track);
        }
    }
}
