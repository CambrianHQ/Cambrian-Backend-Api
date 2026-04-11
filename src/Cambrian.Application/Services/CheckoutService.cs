using System.Security.Claims;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Pricing;
using Cambrian.Domain.Constants;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
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
    private readonly ILicenseService _licenseService;
    private readonly ITransactionManager _transactions;
    private readonly IEmailService _email;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IConfiguration _config;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ILogger<CheckoutService> _logger;
    private readonly IInvoiceRepository? _invoices;
    private readonly string _frontendUrl;

    public CheckoutService(
        IPaymentGateway gateway,
        ITrackRepository tracks,
        IPurchaseRepository purchases,
        ILibraryRepository library,
        IWalletRepository wallet,
        ILicenseService licenseService,
        ITransactionManager transactions,
        IEmailService email,
        ISubscriptionRepository subscriptions,
        IConfiguration configuration,
        UserManager<ApplicationUser> users,
        ILogger<CheckoutService> logger,
        IInvoiceRepository? invoices = null)
    {
        _gateway = gateway;
        _tracks = tracks;
        _purchases = purchases;
        _library = library;
        _wallet = wallet;
        _licenseService = licenseService;
        _transactions = transactions;
        _email = email;
        _subscriptions = subscriptions;
        _config = configuration;
        _users = users;
        _logger = logger;
        _invoices = invoices;
        _frontendUrl = configuration["App:FrontendUrl"]
            ?? throw new InvalidOperationException("App:FrontendUrl must be configured. Checkout redirects require a valid frontend URL.");
    }

    public async Task<CheckoutResponse> CreateCheckoutAsync(CheckoutRequest request, ClaimsPrincipal user)
    {
        if (!Guid.TryParse(request.TrackId, out var trackId))
            throw new ArgumentException("trackId must be a valid GUID.");

        var track = await _tracks.GetByIdAsync(trackId);

        if (track is null)
            throw new KeyNotFoundException($"Track {request.TrackId} not found.");

        if (request.LicenseType == "exclusive" && track.ExclusiveSold)
            throw new InvalidOperationException("This track has already been sold under an exclusive license.");

        if (request.LicenseType == "copyright_buyout" && (track.ExclusiveSold || track.Status == "copyright_transferred"))
            throw new InvalidOperationException("This track is no longer available for purchase.");

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            throw new UnauthorizedAccessException("User is not authenticated.");

        var requireSub = _config.GetValue<bool>("Checkout:RequireSubscription", true);
        if (requireSub)
        {
            var activeSub = await _subscriptions.GetActiveAsync(userId);
            if (activeSub is null || activeSub.Status != "active")
                throw new ForbiddenException("subscription_required");
        }

        if (string.Equals(track.CreatorId, userId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("You cannot purchase your own track.");

        // ── Reject if user already has a completed purchase for this track+license ──

        var existingPurchases = await _purchases.GetByBuyerIdAsync(userId);
        var duplicate = existingPurchases
            .FirstOrDefault(p => p.TrackId == trackId
                              && p.LicenseType == request.LicenseType
                              && p.Status == PurchaseStatuses.Completed);
        if (duplicate is not null)
            throw new InvalidOperationException("You already own this license for this track.");

        // Determine price based on license type
        var amountCents = request.LicenseType switch
        {
            "exclusive" => track.ExclusivePriceCents > 0 ? track.ExclusivePriceCents : (int)(track.Price * 100),
            "non-exclusive" => track.NonExclusivePriceCents > 0 ? track.NonExclusivePriceCents : (int)(track.Price * 100),
            "copyright_buyout" => track.CopyrightBuyoutPriceCents > 0 ? track.CopyrightBuyoutPriceCents : (track.ExclusivePriceCents > 0 ? track.ExclusivePriceCents : (int)(track.Price * 100)),
            _ => (int)(track.Price * 100)
        };

        var userEmail = user.FindFirstValue(ClaimTypes.Email)
                     ?? user.FindFirstValue("email");

        // Build redirect URLs that match the frontend routes
        var encodedTrackId = Uri.EscapeDataString(request.TrackId);
        var successUrl = $"{_frontendUrl}/checkout/success?trackId={encodedTrackId}&session_id={{CHECKOUT_SESSION_ID}}";
        var cancelUrl = $"{_frontendUrl}/marketplace";

        var url = await _gateway.CreateCheckoutSessionAsync(
            amountCents,
            track.Title,
            clientReferenceId: $"{userId}:{request.TrackId}:{request.LicenseType}:{request.UsageType ?? "personal"}",
            successUrl,
            cancelUrl,
            customerEmail: userEmail);

        return new CheckoutResponse
        {
            CheckoutUrl = url,
            Status = "created",
            DisplayPrice = $"${amountCents / 100m:F2}",
            Currency = "usd"
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

        // Parse clientReferenceId: "userId:trackId:licenseType:usageType"
        var parts = session.ClientReferenceId?.Split(':');
        if (parts is null || parts.Length < 3)
        {
            _logger.LogWarning("Invalid clientReferenceId format: {Ref}", session.ClientReferenceId);
            return new CheckoutConfirmResponse { Status = "failed", SessionId = sessionId };
        }

        var sessionUserId = parts[0];
        var trackIdStr = parts[1];
        var licenseType = parts[2];
        var usageType = parts.Length >= 4 ? parts[3] : "personal";

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

        // ── PAY-C2 guard: check if webhook already fulfilled this session ──
        var alreadyFulfilled = await _purchases.GetByStripeSessionIdAsync(sessionId);
        if (alreadyFulfilled is not null)
        {
            // Ensure library row exists even on duplicate path
            var existingLibSess = await _library.GetByUserAndTrackAsync(userId, trackId);
            if (existingLibSess is null)
            {
                var backfillItem = new LibraryItem
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    TrackId = trackId,
                    PurchaseId = alreadyFulfilled.Id,
                    Title = track.Title,
                    Artist = track.Creator?.DisplayName ?? "",
                    AudioUrl = track.AudioUrl,
                    SavedAt = DateTime.UtcNow
                };
                await _library.AddAsync(backfillItem);
            }

            return new CheckoutConfirmResponse
            {
                Status = "paid",
                TrackId = trackIdStr,
                LicenseType = licenseType,
                AddedToLibrary = true,
                SessionId = sessionId,
                LicenseId = alreadyFulfilled.LicenseId?.ToString()
            };
        }

        // ── Idempotent: check for existing purchase ──
        var existingPurchases = await _purchases.GetByBuyerIdAsync(userId);
        var existingPurchase = existingPurchases
            .FirstOrDefault(p => p.TrackId == trackId && p.LicenseType == licenseType);

        if (existingPurchase is not null)
        {
            // Already fulfilled — ensure it's marked completed
            if (existingPurchase.Status != PurchaseStatuses.Completed)
            {
                existingPurchase.Status = PurchaseStatuses.Completed;
                await _purchases.UpdateAsync(existingPurchase);
            }

            // Ensure library row exists even on duplicate purchase path
            var existingLibDup = await _library.GetByUserAndTrackAsync(userId, trackId);
            if (existingLibDup is null)
            {
                var backfillItem = new LibraryItem
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    TrackId = trackId,
                    PurchaseId = existingPurchase.Id,
                    Title = track.Title,
                    Artist = track.Creator?.DisplayName ?? "",
                    AudioUrl = track.AudioUrl,
                    SavedAt = DateTime.UtcNow
                };
                await _library.AddAsync(backfillItem);
                _logger.LogInformation("Library item back-filled for duplicate purchase: User={UserId} Track={TrackId}", userId, trackId);
            }
            else if (existingLibDup.PurchaseId is null)
            {
                existingLibDup.PurchaseId = existingPurchase.Id;
                await _library.UpdateAsync(existingLibDup);
            }

            return new CheckoutConfirmResponse
            {
                Status = "paid",
                TrackId = trackIdStr,
                LicenseType = licenseType,
                AddedToLibrary = true,
                SessionId = sessionId,
                LicenseId = existingPurchase.LicenseId?.ToString()
            };
        }

        // ── Wrap fulfillment in a transaction (PAY-C4) ──
        await using var txHandle = await _transactions.BeginTransactionAsync();
        try
        {
            // ── Mark exclusive if applicable (PAY-C5: atomic CAS) ──
            if (licenseType == "exclusive")
            {
                var exclusiveMarked = await _tracks.TryMarkExclusiveSoldAsync(trackId);
                if (!exclusiveMarked)
                {
                    _logger.LogWarning("Exclusive race in ConfirmAsync: Track {TrackId} was already sold exclusively — skipping for user {UserId}", trackId, userId);
                    await _transactions.RollbackAsync();
                    return new CheckoutConfirmResponse { Status = "failed", SessionId = sessionId };
                }
            }

            // ── Mark copyright_buyout if applicable (atomic CAS — matches StripeWebhookService pattern) ──
            if (licenseType == "copyright_buyout")
            {
                var buyoutMarked = await _tracks.TryMarkCopyrightBuyoutAsync(trackId, userId);
                if (!buyoutMarked)
                {
                    _logger.LogWarning("Copyright buyout race in ConfirmAsync: Track {TrackId} already sold/transferred — skipping for user {UserId}", trackId, userId);
                    await _transactions.RollbackAsync();
                    return new CheckoutConfirmResponse { Status = "failed", SessionId = sessionId };
                }
            }

            // ── Create Purchase record ──
            // Stripe AmountTotal is long. Purchase.AmountCents is int (~$21.4M ceiling).
            // Refuse to silently truncate — fail the confirm so the buyer is not charged
            // for an unfulfilled purchase. Stripe will retry / manual reconciliation handles
            // the rare overflow case.
            var sessionRawAmount = session.AmountTotal ?? 0;
            if (sessionRawAmount > int.MaxValue || sessionRawAmount < 0)
            {
                _logger.LogError(
                    "[CHECKOUT-OVERFLOW] Session {SessionId} amount {Amount}c exceeds int.MaxValue or is negative — refusing to fulfill.",
                    sessionId, sessionRawAmount);
                await _transactions.RollbackAsync();
                return new CheckoutConfirmResponse { Status = "failed", SessionId = sessionId };
            }

            var purchase = new Purchase
            {
                Id = Guid.NewGuid(),
                BuyerId = userId,
                TrackId = trackId,
                AmountCents = (int)sessionRawAmount,
                PaymentMethod = "stripe",
                LicenseType = licenseType,
                UsageType = usageType,
                Status = PurchaseStatuses.Completed,
                StripeSessionId = sessionId,
                CompletedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };
            await _purchases.AddAsync(purchase);

            if (_invoices is not null)
            {
                await _invoices.AddAsync(new Invoice
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    PurchaseId = purchase.Id,
                    AmountCents = purchase.AmountCents,
                    Currency = "usd",
                    Status = "paid",
                    IssuedAt = DateTime.UtcNow,
                    PaidAt = DateTime.UtcNow
                });
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
                    PurchaseId = purchase.Id,
                    Title = track.Title,
                    Artist = track.Creator?.DisplayName ?? "",
                    AudioUrl = track.AudioUrl,
                    SavedAt = DateTime.UtcNow
                };
                await _library.AddAsync(libraryItem);
            }
            else if (existingLib.PurchaseId is null)
            {
                existingLib.PurchaseId = purchase.Id;
                await _library.UpdateAsync(existingLib);
            }

            // ── Credit creator wallet ──
            if (!string.IsNullOrEmpty(track.CreatorId) && session.AmountTotal is > 0)
            {
                var creatorUser = await _users.FindByIdAsync(track.CreatorId);
                var platformFeeRate = creatorUser is not null
                    ? TierManifest.For(creatorUser.CreatorTier).FeeRate
                    : TierManifest.Free.FeeRate;
                var grossCents = session.AmountTotal.Value;
                // Single source of truth: see CreatorEarningsCalculator. Floors at 0
                // and uses per-purchase Math.Floor so the dashboard total matches the
                // sum of WalletTransaction credits.
                var creatorCents = CreatorEarningsCalculator.ComputeCreatorCents(grossCents, platformFeeRate);

                if (creatorCents > 0)
                {
                    var walletTx = new WalletTransaction
                    {
                        Id = Guid.NewGuid(),
                        UserId = track.CreatorId,
                        AmountCents = creatorCents,
                        Type = "credit",
                        Description = $"Sale: {track.Title} ({licenseType})",
                        RelatedPurchaseId = purchase.Id,
                        CreatedAt = DateTime.UtcNow
                    };
                    await _wallet.AddTransactionAsync(walletTx);
                    _logger.LogInformation(
                        "Credited creator {CreatorId} with {AmountCents} cents for track {TrackId}",
                        track.CreatorId, creatorCents, trackId);
                }
            }

            // ── Issue license certificate ──
            string? licenseId = null;
            try
            {
                var cert = await _licenseService.IssueCertificateAsync(
                    purchase.Id,
                    track.CambrianTrackId ?? trackIdStr,
                    userId,
                    track.CreatorId,
                    licenseType,
                    usageType);
                licenseId = cert.LicenseId;

                if (Guid.TryParse(licenseId, out var licGuid))
                {
                    purchase.LicenseId = licGuid;
                    await _purchases.UpdateAsync(purchase);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[LICENSE-FAILED] Failed to issue license certificate for purchase {PurchaseId} — purchase and library created but license missing. Manual reconciliation required.",
                    purchase.Id);
            }

            await _transactions.CommitAsync();

            // Send purchase confirmation email (non-critical — after tx commit)
            try
            {
                var buyer = await _users.FindByIdAsync(userId);
                if (buyer?.Email is not null)
                {
                    var pricePaid = (session.AmountTotal ?? 0) / 100m;
                    var licenseUrl = $"{_frontendUrl}/licenses/{licenseId}";
                    await _email.SendPurchaseConfirmationAsync(buyer.Email, track.Title, licenseType, pricePaid, licenseUrl);
                }
            }
            catch (Exception emailEx)
            {
                _logger.LogWarning(emailEx, "Failed to send purchase confirmation email for purchase {PurchaseId} — non-critical", purchase.Id);
            }

            _logger.LogInformation(
                "Purchase confirmed: User={UserId} Track={TrackId} License={License} LicenseId={LicenseId}",
                userId, trackId, licenseType, licenseId);

            return new CheckoutConfirmResponse
            {
                Status = "paid",
                TrackId = trackIdStr,
                LicenseType = licenseType,
                AddedToLibrary = true,
                SessionId = sessionId,
                LicenseId = licenseId
            };
        }
        catch
        {
            await _transactions.RollbackAsync();
            throw;
        }
    }
}
