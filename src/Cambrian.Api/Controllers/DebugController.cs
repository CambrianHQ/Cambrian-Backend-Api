using System.Security.Claims;
using Cambrian.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Admin-only diagnostic endpoints for debugging user state, purchases, and library consistency.
/// </summary>
[Route("debug")]
[Authorize(Roles = "Admin")]
public class DebugController : BaseController
{
    private readonly CambrianDbContext _db;
    private readonly ILogger<DebugController> _logger;

    public DebugController(CambrianDbContext db, ILogger<DebugController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Returns full diagnostic state for a user: profile, tier, subscription, purchases, library items.
    /// </summary>
    [HttpGet("user/{userId}")]
    public async Task<IActionResult> UserState(string userId)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        _logger.LogInformation("EVENT: DebugUserState requested by admin:{AdminId} for user:{UserId}", adminId, userId);

        var user = await _db.Users.FindAsync(userId);
        if (user is null)
            return NotFoundResponse($"User {userId} not found.");

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

        // Consistency check: purchases completed but no matching library item
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

        return OkResponse(new
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
        });
    }

    /// <summary>
    /// Returns recent webhook events with optional filtering.
    /// </summary>
    [HttpGet("webhooks")]
    public async Task<IActionResult> RecentWebhooks([FromQuery] int limit = 25, [FromQuery] string? eventType = null)
    {
        var query = _db.StripeWebhookEvents.AsQueryable();

        if (!string.IsNullOrWhiteSpace(eventType))
            query = query.Where(e => e.EventType == eventType);

        var events = await query
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

        return OkResponse(events);
    }

    /// <summary>
    /// Library consistency check across all users: finds completed purchases with no matching library item.
    /// </summary>
    [HttpGet("consistency")]
    public async Task<IActionResult> ConsistencyCheck()
    {
        _logger.LogInformation("EVENT: ConsistencyCheck started by admin:{AdminId}",
            User.FindFirstValue(ClaimTypes.NameIdentifier));

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

        return OkResponse(new
        {
            totalCompletedPurchases = completedPurchases.Count,
            totalLibraryItems = libraryPairs.Count,
            orphanedPurchases = orphaned,
            healthy = orphaned.Count == 0
        });
    }
}
