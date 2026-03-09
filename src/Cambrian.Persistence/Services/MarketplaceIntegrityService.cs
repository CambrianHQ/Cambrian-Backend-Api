using Cambrian.Application.DTOs.Admin;
using Cambrian.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cambrian.Persistence.Services;

public class MarketplaceIntegrityService : IMarketplaceIntegrityService
{
    private readonly CambrianDbContext _db;
    private readonly ILogger<MarketplaceIntegrityService> _logger;

    public MarketplaceIntegrityService(CambrianDbContext db, ILogger<MarketplaceIntegrityService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IntegrityReport> RunAuditAsync()
    {
        var report = new IntegrityReport();

        await CheckCompletedPurchasesHaveLibraryEntries(report);
        await CheckExclusiveSoldTracksNotBrowsable(report);
        await CheckPayoutAmountsMatchRevenue(report);
        await CheckOrphanedLibraryItems(report);
        await CheckCompletedPurchasesHaveInvoices(report);
        await CheckExclusivePurchasesHaveTrackFlag(report);

        report.Summary = new IntegritySummary
        {
            CompletedPurchasesWithoutLibrary = report.Violations
                .Count(v => v.Rule == "purchase-library-link"),
            ExclusiveSoldButBrowsable = report.Violations
                .Count(v => v.Rule == "exclusive-visibility"),
            PayoutAmountMismatches = report.Violations
                .Count(v => v.Rule == "payout-revenue-match"),
            OrphanedLibraryItems = report.Violations
                .Count(v => v.Rule == "orphaned-library-item"),
            PurchasesWithoutInvoice = report.Violations
                .Count(v => v.Rule == "purchase-invoice-link"),
            ExclusivePurchasesWithoutFlag = report.Violations
                .Count(v => v.Rule == "exclusive-purchase-flag"),
        };

        _logger.LogInformation(
            "Marketplace integrity audit complete: {ViolationCount} violations found",
            report.TotalViolations);

        return report;
    }

    /// <summary>
    /// Rule: purchase.completed → library entry must exist for that buyer+track.
    /// </summary>
    private async Task CheckCompletedPurchasesHaveLibraryEntries(IntegrityReport report)
    {
        var completedPurchases = await _db.Purchases
            .Where(p => p.Status == "completed")
            .Select(p => new { p.Id, p.BuyerId, p.TrackId })
            .ToListAsync();

        var libraryKeys = (await _db.Library
            .Select(l => new { l.UserId, l.TrackId })
            .ToListAsync())
            .ToHashSet();

        foreach (var purchase in completedPurchases)
        {
            var hasLibraryEntry = libraryKeys.Contains(
                new { UserId = purchase.BuyerId, purchase.TrackId });

            if (!hasLibraryEntry)
            {
                report.Violations.Add(new IntegrityViolation
                {
                    Rule = "purchase-library-link",
                    Severity = "error",
                    EntityType = "Purchase",
                    EntityId = purchase.Id.ToString(),
                    Description = $"Completed purchase {purchase.Id} for buyer {purchase.BuyerId} " +
                                  $"and track {purchase.TrackId} has no corresponding library entry."
                });
            }
        }
    }

    /// <summary>
    /// Rule: exclusiveSold = true → track must not appear in browse results
    /// (Visibility must not be "public" or ExclusiveSold must be true and properly filtered).
    /// </summary>
    private async Task CheckExclusiveSoldTracksNotBrowsable(IntegrityReport report)
    {
        var exclusiveSoldButVisible = await _db.Tracks
            .Where(t => t.ExclusiveSold && t.Visibility == "public")
            .Select(t => new { t.Id, t.Title })
            .ToListAsync();

        foreach (var track in exclusiveSoldButVisible)
        {
            report.Violations.Add(new IntegrityViolation
            {
                Rule = "exclusive-visibility",
                Severity = "warning",
                EntityType = "Track",
                EntityId = track.Id.ToString(),
                Description = $"Track \"{track.Title}\" ({track.Id}) is marked ExclusiveSold " +
                              "but still has Visibility=\"public\". The BrowseAsync filter handles this, " +
                              "but Visibility should ideally be updated for consistency."
            });
        }
    }

    /// <summary>
    /// Rule: creator payout total must not exceed total purchase revenue for that creator.
    /// </summary>
    private async Task CheckPayoutAmountsMatchRevenue(IntegrityReport report)
    {
        var creatorPayouts = await _db.Payouts
            .Where(p => p.Status == "completed" || p.Status == "approved")
            .GroupBy(p => p.CreatorId)
            .Select(g => new { CreatorId = g.Key, TotalPaid = g.Sum(p => p.Amount) })
            .ToListAsync();

        foreach (var payout in creatorPayouts)
        {
            if (string.IsNullOrWhiteSpace(payout.CreatorId))
            {
                report.Violations.Add(new IntegrityViolation
                {
                    Rule = "payout-revenue-match",
                    Severity = "error",
                    EntityType = "Payout",
                    EntityId = payout.CreatorId ?? "(empty)",
                    Description = "Payout(s) exist with an empty CreatorId — cannot verify revenue linkage."
                });
                continue;
            }

            var creatorTrackIds = await _db.Tracks
                .Where(t => t.CreatorId == payout.CreatorId)
                .Select(t => t.Id)
                .ToListAsync();

            var totalRevenue = await _db.Purchases
                .Where(p => creatorTrackIds.Contains(p.TrackId) && p.Status == "completed")
                .SumAsync(p => p.Amount);

            if (payout.TotalPaid > totalRevenue)
            {
                report.Violations.Add(new IntegrityViolation
                {
                    Rule = "payout-revenue-match",
                    Severity = "error",
                    EntityType = "Payout",
                    EntityId = payout.CreatorId,
                    Description = $"Creator {payout.CreatorId} has been paid {payout.TotalPaid:F2} " +
                                  $"but only earned {totalRevenue:F2} in completed purchases."
                });
            }
        }
    }

    /// <summary>
    /// Rule: library items should correspond to a valid track.
    /// </summary>
    private async Task CheckOrphanedLibraryItems(IntegrityReport report)
    {
        var libraryItems = await _db.Library
            .Select(l => new { l.Id, l.UserId, l.TrackId })
            .ToListAsync();

        var trackIds = (await _db.Tracks.Select(t => t.Id).ToListAsync()).ToHashSet();

        foreach (var item in libraryItems)
        {
            if (!trackIds.Contains(item.TrackId))
            {
                report.Violations.Add(new IntegrityViolation
                {
                    Rule = "orphaned-library-item",
                    Severity = "error",
                    EntityType = "LibraryItem",
                    EntityId = item.Id.ToString(),
                    Description = $"Library item {item.Id} references track {item.TrackId} which no longer exists."
                });
            }
        }
    }

    /// <summary>
    /// Rule: every completed purchase should have a corresponding invoice.
    /// </summary>
    private async Task CheckCompletedPurchasesHaveInvoices(IntegrityReport report)
    {
        var completedPurchaseIds = await _db.Purchases
            .Where(p => p.Status == "completed")
            .Select(p => p.Id)
            .ToListAsync();

        var invoicedPurchaseIds = (await _db.Invoices
            .Select(i => i.PurchaseId)
            .ToListAsync())
            .ToHashSet();

        foreach (var purchaseId in completedPurchaseIds)
        {
            if (!invoicedPurchaseIds.Contains(purchaseId))
            {
                report.Violations.Add(new IntegrityViolation
                {
                    Rule = "purchase-invoice-link",
                    Severity = "warning",
                    EntityType = "Purchase",
                    EntityId = purchaseId.ToString(),
                    Description = $"Completed purchase {purchaseId} has no corresponding invoice."
                });
            }
        }
    }

    /// <summary>
    /// Rule: if a completed exclusive purchase exists for a track, the track's ExclusiveSold flag must be true.
    /// </summary>
    private async Task CheckExclusivePurchasesHaveTrackFlag(IntegrityReport report)
    {
        var exclusivePurchases = await _db.Purchases
            .Where(p => p.LicenseType == "exclusive" && p.Status == "completed")
            .Select(p => new { p.Id, p.TrackId })
            .ToListAsync();

        var trackFlags = (await _db.Tracks
            .Select(t => new { t.Id, t.ExclusiveSold })
            .ToListAsync())
            .ToDictionary(t => t.Id, t => t.ExclusiveSold);

        foreach (var purchase in exclusivePurchases)
        {
            if (trackFlags.TryGetValue(purchase.TrackId, out var sold) && !sold)
            {
                report.Violations.Add(new IntegrityViolation
                {
                    Rule = "exclusive-purchase-flag",
                    Severity = "error",
                    EntityType = "Track",
                    EntityId = purchase.TrackId.ToString(),
                    Description = $"Track {purchase.TrackId} has a completed exclusive purchase " +
                                  $"({purchase.Id}) but ExclusiveSold is false."
                });
            }
        }
    }
}
