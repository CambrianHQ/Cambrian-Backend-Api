using System.Security.Claims;
using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Cambrian.Application.Services;

public class CheckoutService : ICheckoutService
{
    private readonly IPaymentGateway _gateway;
    private readonly ITrackRepository _tracks;
    private readonly string _frontendUrl;

    public CheckoutService(IPaymentGateway gateway, ITrackRepository tracks, IConfiguration configuration)
    {
        _gateway = gateway;
        _tracks = tracks;
        _frontendUrl = configuration["App:FrontendUrl"] ?? "http://localhost:5173";
    }

    public async Task<CheckoutResponse> CreateCheckoutAsync(CheckoutRequest request, ClaimsPrincipal user)
    {
        var track = await _tracks.GetByIdAsync(Guid.Parse(request.TrackId));

        if (track is null)
            throw new KeyNotFoundException($"Track {request.TrackId} not found.");

        // Determine price based on license type
        var amountCents = request.LicenseType switch
        {
            "exclusive" => track.ExclusivePriceCents > 0 ? track.ExclusivePriceCents : (int)(track.Price * 100),
            "non-exclusive" => track.NonExclusivePriceCents > 0 ? track.NonExclusivePriceCents : (int)(track.Price * 100),
            _ => (int)(track.Price * 100)
        };

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);

        // Build redirect URLs that match the frontend routes
        var encodedTrackId = Uri.EscapeDataString(request.TrackId);
        var successUrl = $"{_frontendUrl}/marketplace?view=success&trackId={encodedTrackId}&session_id={{CHECKOUT_SESSION_ID}}";
        var cancelUrl = $"{_frontendUrl}/marketplace";

        var url = await _gateway.CreateCheckoutSessionAsync(
            amountCents,
            track.Title,
            clientReferenceId: $"{userId}:{request.TrackId}:{request.LicenseType}",
            successUrl,
            cancelUrl);

        return new CheckoutResponse
        {
            CheckoutUrl = url,
            Status = "created"
        };
    }
}