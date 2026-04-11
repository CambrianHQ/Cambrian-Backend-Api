using Cambrian.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cambrian.Persistence.Services;

public class DebugService : IDebugService
{
    private readonly CambrianDbContext _db;
    private readonly ILocalDeliveryDebugStore _localDeliveries;
    private readonly ILogger<DebugService> _logger;

    public DebugService(CambrianDbContext db, ILocalDeliveryDebugStore localDeliveries, ILogger<DebugService> logger)
    {
        _db = db;
        _localDeliveries = localDeliveries;
        _logger = logger;
    }

    public async Task<object?> GetUserStateAsync(string userId)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user is null) return null;

        var subscription = await _db.Subscriptions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync();

        var purchases = await _db.Purchases
            .Where(p => p.BuyerId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .Take(50)
            .Select(p => new
            {
                p.Id,
                p.TrackId,
                p.LicenseType,
                p.Status,
                p.AmountCents,
                p.StripeSessionId,
                p.LicenseId,
                p.CompletedAt,
                p.UpdatedAt,
                p.CreatedAt,
                p.ExpiresAt
            })
            .ToListAsync();

        var libraryItems = await _db.Library
            .Where(l => l.UserId == userId)
            .Select(l => new
            {
                l.Id,
                l.TrackId,
                l.Title,
                l.PurchaseId,
                l.SavedAt
            })
            .ToListAsync();

        var completedPurchaseTrackIds = purchases
            .Where(p => p.Status == "completed")
            .Select(p => p.TrackId)
            .ToHashSet();

        var libraryTrackIds = libraryItems.Select(l => l.TrackId).ToHashSet();

        var orphanedPurchases = completedPurchaseTrackIds
            .Except(libraryTrackIds)
            .ToList();

        var recentWebhookEvents = await _db.StripeWebhookEvents
            .OrderByDescending(e => e.ProcessedAt)
            .Take(20)
            .Select(e => new
            {
                e.Id,
                e.EventId,
                e.EventType,
                e.Processed,
                e.ProcessedAt
            })
            .ToListAsync();

        return new
        {
            user = new
            {
                id = user.Id,
                email = user.Email,
                tier = user.Tier,
                role = user.Role
            },
            subscription = subscription is null ? null : new
            {
                subscription.Id,
                subscription.Plan,
                subscription.Status,
                subscription.StartedAt,
                subscription.ExpiresAt
            },
            purchases,
            libraryItems,
            consistency = new
            {
                completedPurchaseCount = completedPurchaseTrackIds.Count,
                libraryItemCount = libraryItems.Count,
                orphanedPurchaseTrackIds = orphanedPurchases,
                healthy = orphanedPurchases.Count == 0
            },
            recentWebhookEvents
        };
    }

    public async Task<object> GetRecentWebhooksAsync(int limit = 25, string? eventType = null)
    {
        var query = _db.StripeWebhookEvents.AsQueryable();

        if (!string.IsNullOrWhiteSpace(eventType))
            query = query.Where(e => e.EventType == eventType);

        return await query
            .OrderByDescending(e => e.ProcessedAt)
            .Take(limit)
            .Select(e => new
            {
                e.Id,
                e.EventId,
                e.EventType,
                e.Processed,
                e.ProcessedAt,
                payloadLength = e.Payload != null ? e.Payload.Length : 0
            })
            .ToListAsync();
    }

    public async Task<object> RunConsistencyCheckAsync()
    {
        var completedPurchases = await _db.Purchases
            .Where(p => p.Status == "completed")
            .Select(p => new { p.Id, p.BuyerId, p.TrackId, p.LicenseType, p.CompletedAt })
            .ToListAsync();

        var libraryPairs = await _db.Library
            .Select(l => new { l.UserId, l.TrackId })
            .ToListAsync();

        var librarySet = libraryPairs
            .Select(l => $"{l.UserId}:{l.TrackId}")
            .ToHashSet();

        var orphaned = completedPurchases
            .Where(p => !librarySet.Contains($"{p.BuyerId}:{p.TrackId}"))
            .ToList();

        if (orphaned.Count > 0)
        {
            _logger.LogWarning("[CONSISTENCY] Found {Count} completed purchases without library items", orphaned.Count);
        }

        return new
        {
            totalCompletedPurchases = completedPurchases.Count,
            totalLibraryItems = libraryPairs.Count,
            orphanedPurchases = orphaned,
            healthy = orphaned.Count == 0
        };
    }

    public Task<object> GetRecentLocalDeliveriesAsync(int limit = 25, string? recipient = null, string? kind = null)
        => Task.FromResult<object>(_localDeliveries.GetRecent(limit, recipient, kind));

    public Task<object?> GetLatestLocalPasswordResetAsync(string? email = null, string? phoneNumber = null)
        => Task.FromResult<object?>(_localDeliveries.GetLatestPasswordReset(email, phoneNumber));
}
