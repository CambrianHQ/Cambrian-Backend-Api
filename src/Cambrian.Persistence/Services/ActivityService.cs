using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Cambrian.Persistence.Services;

public sealed class ActivityService : IActivityService
{
    private readonly CambrianDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IFeatureFlagService _flags;

    public ActivityService(
        CambrianDbContext db,
        IMemoryCache cache,
        IFeatureFlagService flags)
    {
        _db = db;
        _cache = cache;
        _flags = flags;
    }

    public async Task<IReadOnlyList<ActivityItemResponse>> GetNewAsync(CancellationToken ct)
    {
        if (!await _flags.IsEnabledAsync("ActivityFeedEnabled", ct))
            return Array.Empty<ActivityItemResponse>();

        return await _db.ActivityItems
            .AsNoTracking()
            .Where(a => a.Type == "new" && a.TrackId != null)
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(20)
            .Join(
                _db.Tracks.AsNoTracking(),
                a => a.TrackId!,
                t => t.Id,
                (a, t) => new ActivityItemResponse
                {
                    Type = a.Type,
                    CreatedAt = a.CreatedAtUtc,
                    TrackId = t.Id,
                    TrackTitle = t.Title
                })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ActivityItemResponse>> GetSalesAsync(CancellationToken ct)
    {
        if (!await _flags.IsEnabledAsync("ActivityFeedEnabled", ct))
            return Array.Empty<ActivityItemResponse>();

        return await _db.ActivityItems
            .AsNoTracking()
            .Where(a => a.Type == "sale" && a.TrackId != null)
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(20)
            .Join(
                _db.Tracks.AsNoTracking(),
                a => a.TrackId!,
                t => t.Id,
                (a, t) => new ActivityItemResponse
                {
                    Type = a.Type,
                    CreatedAt = a.CreatedAtUtc,
                    TrackId = t.Id,
                    TrackTitle = t.Title
                })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TrendingTrackResponse>> GetTrendingAsync(CancellationToken ct)
    {
        if (!await _flags.IsEnabledAsync("TrendingV2Enabled", ct))
            return Array.Empty<TrendingTrackResponse>();

        return await _cache.GetOrCreateAsync("activity:trending:v1", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);

            var since = DateTime.UtcNow.AddDays(-7);

            var rows = await _db.Tracks
                .AsNoTracking()
                .Select(t => new
                {
                    Track = t,
                    PurchaseCount = _db.Purchases.Count(p => p.TrackId == t.Id && p.CreatedAt >= since),
                    ClickCount = _db.AnalyticsEvents.Count(e =>
                        e.TrackId == t.Id &&
                        e.EventType == "track_click" &&
                        e.CreatedAt >= since)
                })
                .Select(x => new
                {
                    x.Track,
                    Score = (x.PurchaseCount * 5m) + (x.ClickCount * 2m) + (x.Track.CreatedAt >= since ? 1m : 0m)
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Track.CreatedAt)
                .Take(20)
                .Select(x => new TrendingTrackResponse
                {
                    TrackId = x.Track.Id,
                    Title = x.Track.Title,
                    Score = x.Score,
                    UseCase = x.Track.UseCase
                })
                .ToListAsync(ct);

            if (rows.Count > 0)
                return (IReadOnlyList<TrendingTrackResponse>)rows;

            return await _db.Tracks
                .AsNoTracking()
                .OrderByDescending(t => t.CreatedAt)
                .Take(20)
                .Select(t => new TrendingTrackResponse
                {
                    TrackId = t.Id,
                    Title = t.Title,
                    Score = 0,
                    UseCase = t.UseCase
                })
                .ToListAsync(ct);
        }) ?? Array.Empty<TrendingTrackResponse>();
    }
}
