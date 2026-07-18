using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Services;

public sealed class ActivityService : IActivityService
{
    private readonly CambrianDbContext _db;
    private readonly IFeatureFlagService _flags;

    public ActivityService(
        CambrianDbContext db,
        IFeatureFlagService flags)
    {
        _db = db;
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
        var rows = await _db.Tracks
            .AsNoTracking()
            .Where(t => t.Visibility == "public"
                        && !t.ExclusiveSold
                        && (t.Status == "available" || t.Status == "active")
                        && t.DeletedAt == null
                        && t.PurgeRequestedAt == null
                        && t.PurgedAt == null
                        && t.AudioUrl != null
                        && t.AudioUrl.Trim() != string.Empty)
            .GroupJoin(
                _db.TrackStats.AsNoTracking(),
                t => t.Id,
                s => s.TrackId,
                (track, stats) => new
                {
                    Track = track,
                    PlayCount = stats.Select(s => s.PlayCount).FirstOrDefault(),
                })
            .OrderByDescending(x => x.PlayCount)
            .ThenByDescending(x => x.Track.CreatedAt)
            .ThenBy(x => x.Track.Id)
            .Take(20)
            .Select(x => new
            {
                TrackId = x.Track.Id,
                x.Track.Title,
                x.Track.UseCase,
                x.PlayCount,
            })
            .ToListAsync(ct);

        return rows.Select(x => new TrendingTrackResponse
        {
            TrackId = x.TrackId,
            Title = x.Title,
            Score = x.PlayCount,
            Plays = x.PlayCount,
            UseCase = x.UseCase,
        }).ToList();
    }
}
