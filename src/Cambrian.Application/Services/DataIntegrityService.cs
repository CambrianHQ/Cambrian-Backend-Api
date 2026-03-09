using Cambrian.Application.DTOs.DataIntegrity;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

public class DataIntegrityService : IDataIntegrityService
{
    private readonly IPurchaseRepository _purchases;
    private readonly ILibraryRepository _library;
    private readonly ITrackRepository _tracks;
    private readonly IPayoutRepository _payouts;
    private readonly ILogger<DataIntegrityService> _logger;

    public DataIntegrityService(
        IPurchaseRepository purchases,
        ILibraryRepository library,
        ITrackRepository tracks,
        IPayoutRepository payouts,
        ILogger<DataIntegrityService> logger)
    {
        _purchases = purchases;
        _library = library;
        _tracks = tracks;
        _payouts = payouts;
        _logger = logger;
    }

    public async Task<DataIntegrityReport> RunFullAuditAsync()
    {
        _logger.LogInformation("Starting full data integrity audit");

        var violations = new List<IntegrityViolation>();

        violations.AddRange(await CheckPurchaseLibraryConsistencyAsync());
        violations.AddRange(await CheckExclusiveLicensingIntegrityAsync());
        violations.AddRange(await CheckPayoutIntegrityAsync());
        violations.AddRange(await CheckOrphanedPayoutsAsync());

        var report = new DataIntegrityReport
        {
            GeneratedAt = DateTime.UtcNow,
            Violations = violations,
            SummaryByCategory = violations
                .GroupBy(v => v.Category)
                .ToDictionary(g => g.Key, g => g.Count())
        };

        _logger.LogInformation(
            "Data integrity audit complete: {ViolationCount} violations found, healthy={IsHealthy}",
            report.TotalViolations, report.IsHealthy);

        return report;
    }

    /// <summary>
    /// Validates: purchase.completed → library entry must exist
    /// Also detects library entries with no corresponding completed purchase.
    /// </summary>
    public async Task<List<IntegrityViolation>> CheckPurchaseLibraryConsistencyAsync()
    {
        var violations = new List<IntegrityViolation>();
        var allTracks = await _tracks.BrowseAsync();
        var allTrackIds = allTracks.Select(t => t.Id).ToHashSet();

        foreach (var track in allTracks)
        {
            var purchases = await _purchases.GetByTrackIdAsync(track.Id);
            var completedPurchases = purchases.Where(p => p.Status == "completed").ToList();

            foreach (var purchase in completedPurchases)
            {
                var libraryItem = await _library.GetByUserAndTrackAsync(purchase.BuyerId, purchase.TrackId);
                if (libraryItem is null)
                {
                    violations.Add(new IntegrityViolation
                    {
                        Category = "PurchaseLibrarySync",
                        Severity = "critical",
                        Description = "Completed purchase exists without corresponding library entry",
                        EntityType = "Purchase",
                        EntityId = purchase.Id.ToString(),
                        Details = new Dictionary<string, object?>
                        {
                            ["buyerId"] = purchase.BuyerId,
                            ["trackId"] = purchase.TrackId.ToString(),
                            ["trackTitle"] = track.Title,
                            ["purchaseAmount"] = purchase.Amount,
                            ["licenseType"] = purchase.LicenseType
                        }
                    });
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Validates:
    /// - exclusiveSold = true → track must not appear in browse results
    /// - Exclusive purchase exists → track.ExclusiveSold must be true
    /// - ExclusiveSold = true → at least one exclusive purchase must exist
    /// </summary>
    public async Task<List<IntegrityViolation>> CheckExclusiveLicensingIntegrityAsync()
    {
        var violations = new List<IntegrityViolation>();
        var browsableTracks = await _tracks.BrowseAsync();
        var browsableIds = browsableTracks.Select(t => t.Id).ToHashSet();

        foreach (var track in browsableTracks)
        {
            var purchases = await _purchases.GetByTrackIdAsync(track.Id);
            var hasExclusivePurchase = purchases.Any(p =>
                p.Status == "completed" &&
                string.Equals(p.LicenseType, "exclusive", StringComparison.OrdinalIgnoreCase));

            if (hasExclusivePurchase && !track.ExclusiveSold)
            {
                violations.Add(new IntegrityViolation
                {
                    Category = "ExclusiveLicensing",
                    Severity = "critical",
                    Description = "Track has a completed exclusive purchase but ExclusiveSold flag is false",
                    EntityType = "Track",
                    EntityId = track.Id.ToString(),
                    Details = new Dictionary<string, object?>
                    {
                        ["trackTitle"] = track.Title,
                        ["exclusivePurchaseCount"] = purchases.Count(p =>
                            p.Status == "completed" &&
                            string.Equals(p.LicenseType, "exclusive", StringComparison.OrdinalIgnoreCase))
                    }
                });
            }

            if (track.ExclusiveSold && browsableIds.Contains(track.Id))
            {
                violations.Add(new IntegrityViolation
                {
                    Category = "ExclusiveLicensing",
                    Severity = "critical",
                    Description = "Exclusively sold track is still appearing in browse results",
                    EntityType = "Track",
                    EntityId = track.Id.ToString(),
                    Details = new Dictionary<string, object?>
                    {
                        ["trackTitle"] = track.Title,
                        ["visibility"] = track.Visibility
                    }
                });
            }
        }

        return violations;
    }

    /// <summary>
    /// Validates: creator payout → must not exceed total purchase revenue for that creator.
    /// </summary>
    public async Task<List<IntegrityViolation>> CheckPayoutIntegrityAsync()
    {
        var violations = new List<IntegrityViolation>();
        var allTracks = await _tracks.BrowseAsync();

        var creatorRevenue = new Dictionary<string, double>();
        foreach (var track in allTracks)
        {
            var purchases = await _purchases.GetByTrackIdAsync(track.Id);
            var trackRevenue = purchases
                .Where(p => p.Status == "completed")
                .Sum(p => p.Amount);

            if (!creatorRevenue.ContainsKey(track.CreatorId))
                creatorRevenue[track.CreatorId] = 0;
            creatorRevenue[track.CreatorId] += trackRevenue;
        }

        foreach (var (creatorId, revenue) in creatorRevenue)
        {
            var payouts = await _payouts.GetByCreatorIdAsync(creatorId);
            var totalPaidOut = payouts
                .Where(p => p.Status is "completed" or "approved" or "pending")
                .Sum(p => p.Amount);

            if (totalPaidOut > revenue)
            {
                violations.Add(new IntegrityViolation
                {
                    Category = "PayoutRevenueMismatch",
                    Severity = "critical",
                    Description = "Total payouts exceed total purchase revenue for creator",
                    EntityType = "Creator",
                    EntityId = creatorId,
                    Details = new Dictionary<string, object?>
                    {
                        ["totalRevenue"] = revenue,
                        ["totalPayouts"] = totalPaidOut,
                        ["overage"] = totalPaidOut - revenue
                    }
                });
            }
        }

        return violations;
    }

    /// <summary>
    /// Detects payouts with empty or missing CreatorId.
    /// </summary>
    public async Task<List<IntegrityViolation>> CheckOrphanedPayoutsAsync()
    {
        var violations = new List<IntegrityViolation>();
        var allTracks = await _tracks.BrowseAsync();
        var creatorIds = allTracks.Select(t => t.CreatorId).Where(id => !string.IsNullOrEmpty(id)).ToHashSet();

        foreach (var creatorId in creatorIds)
        {
            var payouts = await _payouts.GetByCreatorIdAsync(creatorId);
            foreach (var payout in payouts)
            {
                if (string.IsNullOrWhiteSpace(payout.CreatorId))
                {
                    violations.Add(new IntegrityViolation
                    {
                        Category = "OrphanedPayout",
                        Severity = "warning",
                        Description = "Payout record has an empty CreatorId",
                        EntityType = "Payout",
                        EntityId = payout.Id.ToString(),
                        Details = new Dictionary<string, object?>
                        {
                            ["amount"] = payout.Amount,
                            ["status"] = payout.Status,
                            ["requestedAt"] = payout.RequestedAt
                        }
                    });
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Auto-repair: create missing library entries for completed purchases.
    /// Returns the number of entries created.
    /// </summary>
    public async Task<int> RepairMissingLibraryEntriesAsync()
    {
        int repaired = 0;
        var allTracks = await _tracks.BrowseAsync();

        foreach (var track in allTracks)
        {
            var purchases = await _purchases.GetByTrackIdAsync(track.Id);
            var completedPurchases = purchases.Where(p => p.Status == "completed").ToList();

            foreach (var purchase in completedPurchases)
            {
                var existing = await _library.GetByUserAndTrackAsync(purchase.BuyerId, purchase.TrackId);
                if (existing is null)
                {
                    await _library.AddAsync(new LibraryItem
                    {
                        Id = Guid.NewGuid(),
                        UserId = purchase.BuyerId,
                        TrackId = purchase.TrackId,
                        Title = track.Title,
                        Artist = track.Creator?.DisplayName ?? track.Creator?.Email,
                        AudioUrl = track.AudioUrl,
                        SavedAt = DateTime.UtcNow
                    });
                    repaired++;

                    _logger.LogInformation(
                        "Repaired missing library entry: User={UserId} Track={TrackId}",
                        purchase.BuyerId, purchase.TrackId);
                }
            }
        }

        return repaired;
    }

    /// <summary>
    /// Auto-repair: set ExclusiveSold=true for tracks that have completed exclusive purchases.
    /// Returns the number of tracks fixed.
    /// </summary>
    public async Task<int> RepairExclusiveFlagsAsync()
    {
        int repaired = 0;
        var allTracks = await _tracks.BrowseAsync();

        foreach (var track in allTracks)
        {
            if (track.ExclusiveSold) continue;

            var purchases = await _purchases.GetByTrackIdAsync(track.Id);
            var hasExclusivePurchase = purchases.Any(p =>
                p.Status == "completed" &&
                string.Equals(p.LicenseType, "exclusive", StringComparison.OrdinalIgnoreCase));

            if (hasExclusivePurchase)
            {
                track.ExclusiveSold = true;
                await _tracks.UpdateAsync(track);
                repaired++;

                _logger.LogInformation(
                    "Repaired ExclusiveSold flag: Track={TrackId} ({Title})",
                    track.Id, track.Title);
            }
        }

        return repaired;
    }
}
