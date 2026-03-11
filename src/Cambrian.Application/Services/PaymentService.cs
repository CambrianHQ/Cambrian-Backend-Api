using Cambrian.Application.DTOs.Payments;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

public class PaymentService : IPaymentService
{
    private readonly IPaymentGateway _gateway;
    private readonly ITrackRepository _tracks;
    private readonly IPurchaseRepository _purchases;

    public PaymentService(IPaymentGateway gateway, ITrackRepository tracks, IPurchaseRepository purchases)
    {
        _gateway = gateway;
        _tracks = tracks;
        _purchases = purchases;
    }

    public async Task<PaymentCheckoutResponse> CreateCheckoutAsync(PaymentCheckoutRequest request, string? customerEmail = null)
    {
        if (string.IsNullOrWhiteSpace(request.TrackId))
            throw new ArgumentException("TrackId is required.");

        var track = await _tracks.GetByIdAsync(Guid.Parse(request.TrackId))
                    ?? throw new KeyNotFoundException($"Track {request.TrackId} not found.");

        var amountCents = (int)(track.Price * 100);

        var url = await _gateway.CreateCheckoutSessionAsync(
            amountCents,
            track.Title,
            clientReferenceId: request.ClientReferenceId ?? request.TrackId,
            customerEmail: customerEmail);

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

    public async Task ProcessAsync(PaymentProcessRequest request, string userId)
    {
        var purchase = await _purchases.GetByIdAsync(Guid.Parse(request.PurchaseId))
                       ?? throw new KeyNotFoundException($"Purchase {request.PurchaseId} not found.");

        if (purchase.BuyerId != userId)
            throw new UnauthorizedAccessException("You do not own this purchase.");

        purchase.Status = "completed";
        purchase.PaymentMethod = request.PaymentMethodId ?? "stripe";
        await _purchases.UpdateAsync(purchase);
    }
}
