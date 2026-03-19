using Cambrian.Application.DTOs.Payments;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

public class PaymentService : IPaymentService
{
    private readonly IPaymentGateway _gateway;
    private readonly ITrackRepository _tracks;
    private readonly IPurchaseRepository _purchases;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(IPaymentGateway gateway, ITrackRepository tracks, IPurchaseRepository purchases, ILogger<PaymentService> logger)
    {
        _gateway = gateway;
        _tracks = tracks;
        _purchases = purchases;
        _logger = logger;
    }

    public async Task<PaymentCheckoutResponse> CreateCheckoutAsync(PaymentCheckoutRequest request, string userId, string? customerEmail = null)
    {
        if (string.IsNullOrWhiteSpace(request.TrackId))
            throw new ArgumentException("TrackId is required.");

        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId is required.");

        var track = await _tracks.GetByIdAsync(Guid.Parse(request.TrackId))
                    ?? throw new KeyNotFoundException($"Track {request.TrackId} not found.");

        var amountCents = request.LicenseType switch
        {
            "exclusive" => track.ExclusivePriceCents > 0 ? track.ExclusivePriceCents : (int)(track.Price * 100),
            "non-exclusive" => track.NonExclusivePriceCents > 0 ? track.NonExclusivePriceCents : (int)(track.Price * 100),
            "copyright_buyout" => track.CopyrightBuyoutPriceCents > 0
                ? track.CopyrightBuyoutPriceCents
                : (track.ExclusivePriceCents > 0 ? track.ExclusivePriceCents : (int)(track.Price * 100)),
            _ => (int)(track.Price * 100)
        };

        // Build standardized clientReferenceId: userId:trackId:licenseType:usageType
        // This format is required by StripeWebhookService.HandleCheckoutCompleted
        var clientReferenceId = $"{userId}:{request.TrackId}:{request.LicenseType}:{request.UsageType}";

        var url = await _gateway.CreateCheckoutSessionAsync(
            amountCents,
            track.Title,
            clientReferenceId: clientReferenceId,
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

        _logger.LogWarning(
            "[LEGACY-PATH] ProcessAsync only marks purchase status — library/license creation is handled by Stripe webhook or CheckoutService.ConfirmAsync. PurchaseId={PurchaseId} UserId={UserId}",
            purchase.Id, userId);

        purchase.Status = "completed";
        purchase.PaymentMethod = request.PaymentMethodId ?? "stripe";
        await _purchases.UpdateAsync(purchase);
    }
}
