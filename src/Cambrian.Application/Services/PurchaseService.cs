using Cambrian.Application.DTOs.Purchases;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

public class PurchaseService : IPurchaseService
{
    private readonly IPurchaseRepository _purchases;
    private readonly ITrackRepository _tracks;
    private readonly ILibraryRepository _library;
    private readonly IInvoiceRepository _invoiceRepo;

    public PurchaseService(
        IPurchaseRepository purchases,
        ITrackRepository tracks,
        ILibraryRepository library,
        IInvoiceRepository invoiceRepo)
    {
        _purchases = purchases;
        _tracks = tracks;
        _library = library;
        _invoiceRepo = invoiceRepo;
    }

    public async Task<PurchaseResponse> CreateAsync(PurchaseCreateRequest request, string userId)
    {
        if (!Guid.TryParse(request.TrackId, out var trackId))
            throw new ArgumentException("Invalid trackId.");

        var track = await _tracks.GetByIdAsync(trackId)
            ?? throw new KeyNotFoundException("Track not found.");

        var licenseType = string.IsNullOrWhiteSpace(request.LicenseType)
            ? "non-exclusive"
            : request.LicenseType.Trim().ToLowerInvariant();

        if (licenseType == "exclusive" && track.ExclusiveSold)
            throw new InvalidOperationException("This exclusive license has already been sold.");

        // Duplicate check
        var existing = await _purchases.GetByBuyerIdAsync(userId);
        if (existing.Any(p => p.TrackId == trackId && (p.LicenseType ?? "non-exclusive") == licenseType))
            throw new InvalidOperationException("You already own this track.");

        var amountCents = licenseType switch
        {
            "exclusive" => track.ExclusivePriceCents > 0
                ? track.ExclusivePriceCents
                : (int)Math.Round(track.Price * 100, MidpointRounding.AwayFromZero),
            _ => track.NonExclusivePriceCents > 0
                ? track.NonExclusivePriceCents
                : (int)Math.Round(track.Price * 100, MidpointRounding.AwayFromZero)
        };

        // Create purchase record
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = userId,
            TrackId = trackId,
            Amount = amountCents / 100.0,
            LicenseType = licenseType,
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
        if (licenseType == "exclusive")
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
            AmountCents = amountCents,
            Currency = "usd",
            Status = "paid",
            IssuedAt = DateTime.UtcNow,
            PaidAt = DateTime.UtcNow
        };
        await _invoiceRepo.AddAsync(invoice);

        return new PurchaseResponse
        {
            Id = purchase.Id.ToString(),
            TrackId = purchase.TrackId.ToString(),
            TrackTitle = track.Title,
            AmountCents = amountCents,
            LicenseType = purchase.LicenseType ?? "non-exclusive",
            Status = purchase.Status,
            CreatedAt = purchase.CreatedAt,
            CompletedAt = DateTime.UtcNow
        };
    }

    public async Task<IReadOnlyCollection<PurchaseResponse>> GetByBuyerAsync(string userId)
    {
        var purchases = await _purchases.GetByBuyerIdAsync(userId);
        return purchases.Select(p => new PurchaseResponse
        {
            Id = p.Id.ToString(),
            TrackId = p.TrackId.ToString(),
            AmountCents = (int)(p.Amount * 100),
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
