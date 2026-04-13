using Cambrian.Application.DTOs.Purchases;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

public class PurchaseService : IPurchaseService
{
    private readonly IPurchaseRepository _purchases;
    private readonly ITrackRepository _tracks;
    private readonly ILibraryRepository _library;
    private readonly IInvoiceRepository _invoiceRepo;
    private readonly ILicenseService _licenseService;
    private readonly IPaymentGateway _gateway;
    private readonly ITransactionManager _transactions;
    private readonly ILogger<PurchaseService> _logger;

    public PurchaseService(
        IPurchaseRepository purchases,
        ITrackRepository tracks,
        ILibraryRepository library,
        IInvoiceRepository invoiceRepo,
        ILicenseService licenseService,
        IPaymentGateway gateway,
        ITransactionManager transactions,
        ILogger<PurchaseService> logger)
    {
        _purchases = purchases;
        _tracks = tracks;
        _library = library;
        _invoiceRepo = invoiceRepo;
        _licenseService = licenseService;
        _gateway = gateway;
        _transactions = transactions;
        _logger = logger;
    }

    public async Task<PurchaseResponse> CreateAsync(PurchaseCreateRequest request, string userId)
    {
        if (!Guid.TryParse(request.TrackId, out var trackId))
            throw new ArgumentException("Invalid trackId.");

        // ── Verify Stripe payment before creating a purchase ──
        if (string.IsNullOrWhiteSpace(request.StripeSessionId))
            throw new InvalidOperationException("A Stripe checkout session ID is required to complete a purchase.");

        var session = await _gateway.GetCheckoutSessionAsync(request.StripeSessionId);
        if (session is null || session.Status != "paid")
        {
            _logger.LogWarning(
                "Purchase rejected: Stripe session {SessionId} status={Status} for user {UserId}",
                request.StripeSessionId, session?.Status ?? "not_found", userId);
            throw new InvalidOperationException("Payment has not been completed. Please complete checkout first.");
        }

        // ── SECURITY: Verify the Stripe session belongs to this user ──
        if (!string.IsNullOrEmpty(session.ClientReferenceId))
        {
            var refParts = session.ClientReferenceId.Split(':');
            if (refParts.Length >= 1 && refParts[0] != userId)
            {
                _logger.LogWarning(
                    "Purchase rejected: session {SessionId} belongs to user {SessionOwner}, not {RequestingUser}",
                    request.StripeSessionId, refParts[0], userId);
                throw new InvalidOperationException("This payment session does not belong to you.");
            }
        }

        // ── SECURITY: Prevent session replay — check globally, not just for this user ──
        var existingBySession = await _purchases.GetByStripeSessionIdAsync(request.StripeSessionId);
        if (existingBySession is not null)
        {
            _logger.LogWarning(
                "Purchase rejected: Stripe session {SessionId} already used (purchase {PurchaseId}, buyer {BuyerId})",
                request.StripeSessionId, existingBySession.Id, existingBySession.BuyerId);
            throw new InvalidOperationException("This payment session has already been used.");
        }

        var track = await _tracks.GetByIdAsync(trackId)
            ?? throw new KeyNotFoundException("Track not found.");

        if (track.ExclusiveSold)
            throw new InvalidOperationException("This track has already been sold under an exclusive license.");

        // Duplicate check (by track + license type for upgrade paths)
        var existing = await _purchases.GetByBuyerIdAsync(userId);
        if (existing.Any(p => p.TrackId == trackId && p.LicenseType == (request.LicenseType ?? "non-exclusive")))
            throw new InvalidOperationException("You already own this track with this license type.");

        // Atomically mark exclusive BEFORE creating records to prevent race conditions.
        // If two concurrent exclusive requests arrive, only one will succeed here.
        if (request.LicenseType == "exclusive")
        {
            if (!await _tracks.TryMarkExclusiveSoldAsync(trackId))
                throw new InvalidOperationException("This track has already been sold under an exclusive license.");
        }

        // Resolve price in cents based on license type
        var amountCents = (request.LicenseType ?? "non-exclusive") switch
        {
            "exclusive" when track.ExclusivePriceCents > 0 => track.ExclusivePriceCents,
            "non-exclusive" when track.NonExclusivePriceCents > 0 => track.NonExclusivePriceCents,
            _ => (int)Math.Round(track.Price * 100, MidpointRounding.AwayFromZero)
        };

        // ── INVARIANT: purchase amount must be positive ──
        if (amountCents <= 0)
            throw new InvalidOperationException("Track price must be greater than zero to complete a purchase.");

        // ── Wrap fulfillment in a transaction so purchase + library + invoice + license are atomic ──
        await using var txHandle = await _transactions.BeginTransactionAsync();
        try
        {
            // Create purchase record
            var purchase = new Purchase
            {
                Id = Guid.NewGuid(),
                BuyerId = userId,
                TrackId = trackId,
                AmountCents = amountCents,
                LicenseType = request.LicenseType ?? "non-exclusive",
                PaymentMethod = request.PaymentMethod,
                UsageType = request.UsageType ?? "personal",
                Status = "completed",
                StripeSessionId = request.StripeSessionId,
                CreatedAt = DateTime.UtcNow
            };
            await _purchases.AddAsync(purchase);

            // Auto-add to library
            await _library.AddAsync(new LibraryItem
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TrackId = trackId,
                PurchaseId = purchase.Id,
                Title = track.Title,
                Artist = track.Creator?.DisplayName ?? "Unknown",
                AudioUrl = track.AudioUrl,
                SavedAt = DateTime.UtcNow
            });

            // Auto-create invoice
            var invoice = new Invoice
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PurchaseId = purchase.Id,
                AmountCents = purchase.AmountCents,
                Currency = "usd",
                Status = "paid",
                IssuedAt = DateTime.UtcNow,
                PaidAt = DateTime.UtcNow
            };
            await _invoiceRepo.AddAsync(invoice);

            // Issue license certificate
            await _licenseService.IssueCertificateAsync(
                purchase.Id,
                track.CambrianTrackId,
                userId,
                track.CreatorId,
                purchase.LicenseType ?? "non-exclusive",
                purchase.UsageType);

            await _transactions.CommitAsync();

            return new PurchaseResponse
            {
                Id = purchase.Id.ToString(),
                TrackId = purchase.TrackId.ToString(),
                TrackTitle = track.Title,
                AmountCents = purchase.AmountCents,
                LicenseType = purchase.LicenseType ?? "non-exclusive",
                Status = purchase.Status,
                CreatedAt = purchase.CreatedAt,
                CompletedAt = DateTime.UtcNow
            };
        }
        catch
        {
            await _transactions.RollbackAsync();
            throw;
        }
    }

    public async Task<IReadOnlyCollection<PurchaseResponse>> GetByBuyerAsync(string userId)
    {
        var purchases = await _purchases.GetByBuyerIdAsync(userId);

        // Resolve track titles in a single batch to populate TrackTitle
        var trackIds = purchases.Select(p => p.TrackId).Distinct().ToList();
        var trackTitles = new Dictionary<Guid, string>();
        foreach (var trackId in trackIds)
        {
            var track = await _tracks.GetByIdAsync(trackId);
            if (track is not null)
                trackTitles[trackId] = track.Title;
        }

        return purchases.Select(p => new PurchaseResponse
        {
            Id = p.Id.ToString(),
            TrackId = p.TrackId.ToString(),
            TrackTitle = trackTitles.GetValueOrDefault(p.TrackId, ""),
            AmountCents = p.AmountCents,
            LicenseType = p.LicenseType ?? "non-exclusive",
            Status = p.Status,
            CreatedAt = p.CreatedAt,
        }).ToList();
    }

    public Task CreditCreatorAsync(CreditCreatorRequest request)
    {
        // In a real implementation this would credit the creator's wallet.
        // For now, acknowledge the request.
        return Task.CompletedTask;
    }
}
