using System.Security.Claims;
using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

public class CheckoutService : ICheckoutService
{
    private readonly IPaymentGateway _gateway;
    private readonly ITrackRepository _tracks;
    private readonly IPurchaseRepository _purchases;
    private readonly ILibraryRepository _library;
    private readonly IWalletRepository _wallet;
    private readonly ILogger<CheckoutService> _logger;
    private readonly string _frontendUrl;

    public CheckoutService(
        IPaymentGateway gateway,
        ITrackRepository tracks,
        IPurchaseRepository purchases,
        ILibraryRepository library,
        IWalletRepository wallet,
        IConfiguration configuration,
        ILogger<CheckoutService> logger)
    {
        _gateway = gateway;
        _tracks = tracks;
        _purchases = purchases;
        _library = library;
        _wallet = wallet;
        _logger = logger;
        _frontendUrl = configuration["App:FrontendUrl"] ?? "http://localhost:5173";
    }

    public async Task<CheckoutResponse> CreateCheckoutAsync(CheckoutRequest request, ClaimsPrincipal user)
    {
        var track = await _tracks.GetByIdAsync(Guid.Parse(request.TrackId));

        if (track is null)
            throw new KeyNotFoundException($"Track {request.TrackId} not found.");

        if (request.LicenseType == "exclusive" && track.ExclusiveSold)
            throw new InvalidOperationException("This track has already been sold under an exclusive license.");

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

    public async Task<CheckoutConfirmResponse> ConfirmAsync(string sessionId, string userId)
    {
        var session = await _gateway.GetCheckoutSessionAsync(sessionId);
        if (session is null)
        {
            _logger.LogWarning("Checkout session {SessionId} not found in Stripe", sessionId);
            return new CheckoutConfirmResponse { Status = "failed", SessionId = sessionId };
        }

        if (session.Status != "paid")
        {
            return new CheckoutConfirmResponse { Status = session.Status, SessionId = sessionId };
        }

        // Parse clientReferenceId: "userId:trackId:licenseType"
        var parts = session.ClientReferenceId?.Split(':');
        if (parts is null || parts.Length < 3)
        {
            _logger.LogWarning("Invalid clientReferenceId format: {Ref}", session.ClientReferenceId);
            return new CheckoutConfirmResponse { Status = "failed", SessionId = sessionId };
        }

        var sessionUserId = parts[0];
        var trackIdStr = parts[1];
        var licenseType = parts[2];

        // Verify the caller matches the session owner
        if (!string.Equals(sessionUserId, userId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("User {UserId} tried to confirm session belonging to {SessionUserId}",
                userId, sessionUserId);
            return new CheckoutConfirmResponse { Status = "failed", SessionId = sessionId };
        }

        if (!Guid.TryParse(trackIdStr, out var trackId))
        {
            _logger.LogWarning("Invalid trackId in clientReferenceId: {TrackId}", trackIdStr);
            return new CheckoutConfirmResponse { Status = "failed", SessionId = sessionId };
        }

        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null)
        {
            _logger.LogWarning("Track {TrackId} not found during checkout confirm", trackId);
            return new CheckoutConfirmResponse { Status = "failed", SessionId = sessionId };
        }

        // ── Idempotent: check for existing purchase ──
        var existingPurchases = await _purchases.GetByBuyerIdAsync(userId);
        var existingPurchase = existingPurchases
            .FirstOrDefault(p => p.TrackId == trackId && p.LicenseType == licenseType);

        if (existingPurchase is not null)
        {
            // Already fulfilled — ensure it's marked completed
            if (existingPurchase.Status != "completed")
            {
                existingPurchase.Status = "completed";
                await _purchases.UpdateAsync(existingPurchase);
            }

            return new CheckoutConfirmResponse
            {
                Status = "paid",
                TrackId = trackIdStr,
                LicenseType = licenseType,
                AddedToLibrary = true,
                SessionId = sessionId
            };
        }

        // ── Create Purchase record ──
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = userId,
            TrackId = trackId,
            AmountCents = (int)(session.AmountTotal ?? 0),
            PaymentMethod = "stripe",
            LicenseType = licenseType,
            Status = "completed",
            CreatedAt = DateTime.UtcNow
        };
        await _purchases.AddAsync(purchase);

        // ── Mark exclusive if applicable ──
        if (licenseType == "exclusive" && !track.ExclusiveSold)
        {
            track.ExclusiveSold = true;
            await _tracks.UpdateAsync(track);
        }

        // ── Add to library (idempotent) ──
        var existingLib = await _library.GetByUserAndTrackAsync(userId, trackId);
        if (existingLib is null)
        {
            var libraryItem = new LibraryItem
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TrackId = trackId,
                Title = track.Title,
                Artist = track.Creator?.DisplayName ?? "",
                AudioUrl = track.AudioUrl,
                SavedAt = DateTime.UtcNow
            };
            await _library.AddAsync(libraryItem);
        }

        // ── Credit creator wallet (platform takes 15% fee) ──
        if (!string.IsNullOrEmpty(track.CreatorId) && session.AmountTotal is > 0)
        {
            const decimal platformFeeRate = 0.15m;
            var grossCents = session.AmountTotal.Value;
            var creatorCents = (long)Math.Floor(grossCents * (1 - platformFeeRate));

            if (creatorCents > 0)
            {
                var tx = new WalletTransaction
                {
                    Id = Guid.NewGuid(),
                    UserId = track.CreatorId,
                    AmountCents = creatorCents,
                    Type = "credit",
                    Description = $"Sale: {track.Title} ({licenseType})",
                    RelatedPurchaseId = purchase.Id,
                    CreatedAt = DateTime.UtcNow
                };
                await _wallet.AddTransactionAsync(tx);
                _logger.LogInformation(
                    "Credited creator {CreatorId} with {AmountCents} cents for track {TrackId}",
                    track.CreatorId, creatorCents, trackId);
            }
        }

        _logger.LogInformation(
            "Purchase confirmed: User={UserId} Track={TrackId} License={License}",
            userId, trackId, licenseType);

        return new CheckoutConfirmResponse
        {
            Status = "paid",
            TrackId = trackIdStr,
            LicenseType = licenseType,
            AddedToLibrary = true,
            SessionId = sessionId
        };
    }
}