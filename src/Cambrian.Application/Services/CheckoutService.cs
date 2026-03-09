using System.Security.Claims;
using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class CheckoutService : ICheckoutService
{
    private readonly IPaymentGateway _gateway;
    private readonly ITrackRepository _tracks;

    public CheckoutService(IPaymentGateway gateway, ITrackRepository tracks)
    {
        _gateway = gateway;
        _tracks = tracks;
    }

    public async Task<CheckoutResponse> CreateCheckoutAsync(CheckoutRequest request, ClaimsPrincipal user)
    {
        var track = await _tracks.GetByIdAsync(Guid.Parse(request.TrackId));

        if (track is null)
            throw new KeyNotFoundException($"Track {request.TrackId} not found.");

        var licenseType = request.LicenseType.Trim().ToLowerInvariant();
        if (licenseType == "exclusive" && track.ExclusiveSold)
            throw new InvalidOperationException("This exclusive license has already been sold.");

        // Determine price based on license type
        var amountCents = licenseType switch
        {
            "exclusive" => track.ExclusivePriceCents > 0 ? track.ExclusivePriceCents : (int)(track.Price * 100),
            "non-exclusive" => track.NonExclusivePriceCents > 0 ? track.NonExclusivePriceCents : (int)(track.Price * 100),
            _ => (int)(track.Price * 100)
        };

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);

        var url = await _gateway.CreateCheckoutSessionAsync(
            amountCents,
            track.Title,
            clientReferenceId: $"{userId}:{request.TrackId}:{licenseType}");

        return new CheckoutResponse
        {
            CheckoutUrl = url,
            Status = "created"
        };
    }
}