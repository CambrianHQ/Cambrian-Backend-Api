using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

/// <summary>
/// EF Core implementation of the public discovery directory. Every query is scoped to
/// public, non-removed, non-hidden, non-exclusive-sold content via <see cref="PublicTracks"/>.
/// Returns plain value records only — no entities, no storage keys, no private fields leave
/// this class.
/// </summary>
public sealed class PublicDirectoryRepository : IPublicDirectoryRepository
{
    private readonly CambrianDbContext _db;

    public PublicDirectoryRepository(CambrianDbContext db) => _db = db;

    /// <summary>The single definition of "public catalogue" used by every aggregate below.</summary>
    private IQueryable<Track> PublicTracks() =>
        _db.Tracks.AsNoTracking().Where(t =>
            !t.ExclusiveSold &&
            t.Status != "copyright_transferred" &&
            t.Status != "removed" &&
            t.Visibility == "public");

    public async Task<PublicPlatformCounts> GetPlatformCountsAsync()
    {
        var pub = PublicTracks();

        var trackCount = await pub.CountAsync();
        var creatorCount = await pub
            .Where(t => t.CreatorId != "")
            .Select(t => t.CreatorId)
            .Distinct()
            .CountAsync();
        var totalPlays = await _db.StreamSessions.CountAsync();
        var genreCount = await pub
            .Select(t => t.Subgenre ?? t.Genre ?? t.PrimaryGenre)
            .Where(g => g != null && g != "")
            .Select(g => g!.ToLower())
            .Distinct()
            .CountAsync();
        var authorshipRecordCount = await _db.AuthorshipRecords
            .AsNoTracking()
            .Where(r => r.Status == "issued")
            .CountAsync();

        return new PublicPlatformCounts(trackCount, creatorCount, genreCount, totalPlays, authorshipRecordCount);
    }

    public async Task<IReadOnlyList<PublicGenreCount>> GetGenreCountsAsync()
    {
        var grouped = await PublicTracks()
            .GroupBy(t => t.Subgenre ?? t.Genre ?? t.PrimaryGenre)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .ToListAsync();

        return grouped
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new PublicGenreCount(x.Name!.Trim(), x.Count))
            .OrderByDescending(x => x.TrackCount)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<PublicCreatorSearchResult> SearchCreatorsAsync(string? query, int page, int pageSize)
    {
        IQueryable<CreatorProfile> profiles = _db.CreatorProfiles.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim().ToLower();

            // Match against creator identity names (Username / DisplayName live on Creators).
            var matchedUserIds = await _db.Creators.AsNoTracking()
                .Where(c =>
                    (c.Username != null && c.Username.ToLower().Contains(needle)) ||
                    (c.DisplayName != null && c.DisplayName.ToLower().Contains(needle)))
                .Select(c => c.UserId)
                .ToListAsync();

            profiles = profiles.Where(p =>
                p.Slug.ToLower().Contains(needle) ||
                (p.Bio != null && p.Bio.ToLower().Contains(needle)) ||
                (p.Niche != null && p.Niche.ToLower().Contains(needle)) ||
                matchedUserIds.Contains(p.UserId));
        }

        var total = await profiles.CountAsync();

        var pageProfiles = await profiles
            .OrderByDescending(p => p.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var rows = await BuildCreatorRowsAsync(pageProfiles);
        return new PublicCreatorSearchResult(rows, total);
    }

    public async Task<IReadOnlyList<PublicCreatorRow>> GetFeaturedCreatorsAsync(int limit)
    {
        // Rank creators by number of public tracks — a real signal (no dead TrendingScore).
        var topCreators = await PublicTracks()
            .Where(t => t.CreatorId != "")
            .GroupBy(t => t.CreatorId)
            .Select(g => new { CreatorId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(limit * 3) // over-fetch: creators without a public storefront slug are dropped below
            .ToListAsync();

        var userIds = topCreators.Select(x => x.CreatorId).ToList();
        var countByUser = topCreators.ToDictionary(x => x.CreatorId, x => x.Count);

        var profiles = await _db.CreatorProfiles.AsNoTracking()
            .Where(p => userIds.Contains(p.UserId))
            .ToListAsync();
        var profileByUser = new Dictionary<string, CreatorProfile>();
        foreach (var p in profiles)
            profileByUser[p.UserId] = p;

        var creators = await LoadCreatorsByUserAsync(userIds);

        var rows = new List<PublicCreatorRow>(limit);
        foreach (var top in topCreators)
        {
            if (rows.Count >= limit) break;
            if (!profileByUser.TryGetValue(top.CreatorId, out var profile)) continue; // need a public slug to link

            creators.TryGetValue(top.CreatorId, out var creator);
            rows.Add(BuildRow(profile, creator, top.Count));
        }

        return rows;
    }

    public async Task<PublicSitemapData> GetSitemapDataAsync(int maxTracks, int maxCreators)
    {
        var trackRows = await PublicTracks()
            .Where(t => t.CambrianTrackId != "")
            .OrderByDescending(t => t.CreatedAt)
            .Take(maxTracks)
            .Select(t => new { t.CambrianTrackId, t.CreatedAt })
            .ToListAsync();

        var creatorRows = await _db.CreatorProfiles.AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .Take(maxCreators)
            .Select(p => new { p.Slug, p.UpdatedAt })
            .ToListAsync();

        var tracks = trackRows
            .Select(t => new PublicSitemapTrackRef(t.CambrianTrackId, t.CreatedAt))
            .ToList();
        var creators = creatorRows
            .Where(c => !string.IsNullOrWhiteSpace(c.Slug))
            .Select(c => new PublicSitemapCreatorRef(c.Slug, c.UpdatedAt))
            .ToList();

        return new PublicSitemapData(tracks, creators);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<PublicCreatorRow>> BuildCreatorRowsAsync(List<CreatorProfile> pageProfiles)
    {
        if (pageProfiles.Count == 0)
            return Array.Empty<PublicCreatorRow>();

        var userIds = pageProfiles.Select(p => p.UserId).ToList();
        var creators = await LoadCreatorsByUserAsync(userIds);

        var trackCounts = await PublicTracks()
            .Where(t => userIds.Contains(t.CreatorId))
            .GroupBy(t => t.CreatorId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync();
        var countByUser = trackCounts.ToDictionary(x => x.Key, x => x.Count);

        var rows = new List<PublicCreatorRow>(pageProfiles.Count);
        foreach (var profile in pageProfiles)
        {
            creators.TryGetValue(profile.UserId, out var creator);
            countByUser.TryGetValue(profile.UserId, out var count);
            rows.Add(BuildRow(profile, creator, count));
        }
        return rows;
    }

    private async Task<Dictionary<string, Creator>> LoadCreatorsByUserAsync(List<string> userIds)
    {
        var list = await _db.Creators.AsNoTracking()
            .Where(c => userIds.Contains(c.UserId))
            .ToListAsync();
        var map = new Dictionary<string, Creator>();
        foreach (var c in list)
            map[c.UserId] = c;
        return map;
    }

    private static PublicCreatorRow BuildRow(CreatorProfile profile, Creator? creator, int trackCount)
    {
        var displayName =
            !string.IsNullOrWhiteSpace(creator?.DisplayName) ? creator!.DisplayName! :
            !string.IsNullOrWhiteSpace(creator?.Username) ? creator!.Username :
            profile.Slug;

        return new PublicCreatorRow(
            Id: creator?.Id.ToString() ?? profile.Id.ToString(),
            Slug: profile.Slug,
            Username: creator?.Username,
            DisplayName: displayName,
            Niche: profile.Niche,
            Bio: profile.Bio,
            ImageUrl: profile.ProfileImageUrl ?? creator?.ProfileImageUrl,
            UpdatedAt: profile.UpdatedAt,
            TrackCount: trackCount);
    }
}
