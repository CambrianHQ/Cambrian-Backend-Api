using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class AnalyticsRepository : IAnalyticsRepository
{
    private readonly CambrianDbContext _db;

    public AnalyticsRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task RecordAsync(string eventType, string? userId = null, Guid? trackId = null, string? metadata = null)
    {
        _db.AnalyticsEvents.Add(new AnalyticsEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            UserId = userId,
            TrackId = trackId,
            Metadata = metadata,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<AnalyticsEvent>> QueryAsync(string? eventType = null, DateTime? from = null, DateTime? to = null, int limit = 100)
    {
        var query = _db.AnalyticsEvents.AsQueryable();

        if (!string.IsNullOrWhiteSpace(eventType))
            query = query.Where(e => e.EventType == eventType);
        if (from.HasValue)
            query = query.Where(e => e.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.CreatedAt <= to.Value);

        var result = await query
            .OrderByDescending(e => e.CreatedAt)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync();

        return result;
    }

    public async Task<Dictionary<string, int>> GetCountsByTypeAsync(DateTime? from = null, DateTime? to = null)
    {
        var query = _db.AnalyticsEvents.AsQueryable();

        if (from.HasValue)
            query = query.Where(e => e.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.CreatedAt <= to.Value);

        var result = await query
            .GroupBy(e => e.EventType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Type, g => g.Count);

        return result;
    }
}
