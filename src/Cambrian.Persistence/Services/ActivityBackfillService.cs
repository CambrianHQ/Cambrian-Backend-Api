using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cambrian.Persistence.Services;

public sealed class ActivityBackfillService : IActivityBackfillService
{
    private readonly CambrianDbContext _db;
    private readonly ILogger<ActivityBackfillService> _logger;

    public ActivityBackfillService(
        CambrianDbContext db,
        ILogger<ActivityBackfillService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task BackfillAsync(CancellationToken ct)
    {
        await BackfillSalesAsync(ct);
        await BackfillNewTracksAsync(ct);
    }

    private async Task BackfillSalesAsync(CancellationToken ct)
    {
        var rows = await _db.Purchases
            .Where(p => p.Status == "completed"
                && !_db.ActivityItems.Any(a => a.SourceId == p.Id && a.Type == "sale"))
            .Select(p => new ActivityItem
            {
                Id = Guid.NewGuid(),
                Type = "sale",
                TrackId = p.TrackId,
                UserId = p.BuyerId,
                SourceId = p.Id,
                IsSimulated = false,
                CreatedAtUtc = p.CreatedAt
            })
            .ToListAsync(ct);

        if (rows.Count == 0) return;

        _db.ActivityItems.AddRange(rows);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Backfilled {Count} sales activity rows.", rows.Count);
    }

    private async Task BackfillNewTracksAsync(CancellationToken ct)
    {
        var rows = await _db.Tracks
            .Where(t => !_db.ActivityItems.Any(a => a.SourceId == t.Id && a.Type == "new"))
            .Select(t => new ActivityItem
            {
                Id = Guid.NewGuid(),
                Type = "new",
                TrackId = t.Id,
                UserId = t.CreatorId,
                SourceId = t.Id,
                IsSimulated = false,
                CreatedAtUtc = t.CreatedAt
            })
            .ToListAsync(ct);

        if (rows.Count == 0) return;

        _db.ActivityItems.AddRange(rows);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Backfilled {Count} new-track activity rows.", rows.Count);
    }
}
