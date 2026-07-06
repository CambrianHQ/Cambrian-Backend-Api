using System.Text.Json;
using Cambrian.Application.DTOs.CreatorProfile;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Albums;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public sealed class CreatorProfileRepository : ICreatorProfileRepository
{
    private readonly CambrianDbContext _db;

    public CreatorProfileRepository(CambrianDbContext db) => _db = db;

    public async Task<CreatorProfileDto?> GetByUserIdAsync(string userId)
    {
        var p = await _db.CreatorProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId);
        return p is null ? null : await MapToDtoAsync(p);
    }

    public async Task<Dictionary<string, (string? Slug, string? ProfileImageUrl)>> GetSlugsByUserIdsAsync(IEnumerable<string> userIds)
    {
        var ids = userIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<string, (string? Slug, string? ProfileImageUrl)>();

        return await _db.CreatorProfiles.AsNoTracking()
            .Where(p => ids.Contains(p.UserId))
            .ToDictionaryAsync(
                p => p.UserId,
                p => ((string?)p.Slug, p.ProfileImageUrl));
    }

    public async Task<CreatorProfileDto?> GetBySlugAsync(string slug)
    {
        var p = await _db.CreatorProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Slug.ToLower() == slug.ToLower());
        return p is null ? null : await MapToDtoAsync(p);
    }

    public async Task<CreatorProfileDto> UpsertAsync(string userId, string slug, string bio, string? niche,
        string? socialLinksJson, bool showEarnings, bool showDownloadStats,
        string? bannerImageUrl = null, string? profileImageUrl = null,
        string? studioSetupJson = null, string? journeyEntriesJson = null)
    {
        var existing = await _db.CreatorProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (existing is null)
        {
            var profile = new CreatorProfile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Slug = slug,
                Bio = bio,
                Niche = niche,
                SocialLinks = socialLinksJson,
                StudioSetup = studioSetupJson,
                JourneyEntries = journeyEntriesJson,
                ShowEarnings = showEarnings,
                ShowDownloadStats = showDownloadStats,
                BannerImageUrl = bannerImageUrl,
                ProfileImageUrl = profileImageUrl,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.CreatorProfiles.Add(profile);
            await _db.SaveChangesAsync();
            return await MapToDtoAsync(profile);
        }

        existing.Slug = slug;
        existing.Bio = bio;
        existing.Niche = niche;
        existing.SocialLinks = socialLinksJson ?? existing.SocialLinks;
        // null = "not sent, keep stored value"; the controller maps an explicit
        // empty object/list to a serialized empty value so creators CAN clear these.
        existing.StudioSetup = studioSetupJson ?? existing.StudioSetup;
        existing.JourneyEntries = journeyEntriesJson ?? existing.JourneyEntries;
        existing.ShowEarnings = showEarnings;
        existing.ShowDownloadStats = showDownloadStats;
        if (bannerImageUrl is not null) existing.BannerImageUrl = bannerImageUrl;
        if (profileImageUrl is not null) existing.ProfileImageUrl = profileImageUrl;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await MapToDtoAsync(existing);
    }

    public async Task<CreatorProfileDto> UpdateImageAsync(string userId, string? bannerImageUrl, string? profileImageUrl)
    {
        var existing = await _db.CreatorProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (existing is null)
        {
            // Auto-create a minimal profile so creators can set an avatar
            // before completing full profile setup. Slug is a placeholder derived
            // from the userId and can be updated later via PUT /creator-profile/me.
            var placeholder = new CreatorProfile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Slug = userId.Replace("-", "").ToLower()[..16],
                Bio = "",
                BannerImageUrl = bannerImageUrl,
                ProfileImageUrl = profileImageUrl,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.CreatorProfiles.Add(placeholder);
            await _db.SaveChangesAsync();
            return await MapToDtoAsync(placeholder);
        }

        if (bannerImageUrl is not null) existing.BannerImageUrl = bannerImageUrl;
        if (profileImageUrl is not null) existing.ProfileImageUrl = profileImageUrl;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await MapToDtoAsync(existing);
    }

    public async Task<CreatorProfileDto> UpdatePinnedTracksAsync(string userId, string pinnedTrackIds)
    {
        var existing = await _db.CreatorProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId);
        if (existing is null) throw new KeyNotFoundException("Profile not found.");

        existing.PinnedTrackIds = pinnedTrackIds;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await MapToDtoAsync(existing);
    }

    public async Task<CreatorProfileDto?> UpdateSettingsAsync(string userId, bool? showEarnings, bool? showDownloadStats)
    {
        var existing = await _db.CreatorProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId);
        if (existing is null) return null;

        if (showEarnings.HasValue) existing.ShowEarnings = showEarnings.Value;
        if (showDownloadStats.HasValue) existing.ShowDownloadStats = showDownloadStats.Value;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await MapToDtoAsync(existing);
    }

    public async Task<CreatorProfileDto?> UpdatePresentationFieldsAsync(string userId, string? bio, string? socialLinksJson, string? bannerImageUrl, string? profileImageUrl)
    {
        var existing = await _db.CreatorProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (existing is null) return null;

        if (bio is not null) existing.Bio = bio;
        if (socialLinksJson is not null) existing.SocialLinks = socialLinksJson;
        if (bannerImageUrl is not null) existing.BannerImageUrl = bannerImageUrl;
        if (profileImageUrl is not null) existing.ProfileImageUrl = profileImageUrl;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await MapToDtoAsync(existing);
    }

    public async Task<IReadOnlyList<TrackCollectionDto>> GetCollectionsAsync(string creatorId)
    {
        var collections = await _db.TrackCollections.AsNoTracking()
            .Where(c => c.CreatorId == creatorId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
        return collections.Select(MapCollectionToDto).ToList();
    }

    public async Task<TrackCollectionDto?> GetCollectionByIdAsync(Guid id)
    {
        var entity = await _db.TrackCollections.FindAsync(id);
        return entity is null ? null : MapCollectionToDto(entity);
    }

    public async Task<string?> GetCollectionOwnerAsync(Guid id)
    {
        var entity = await _db.TrackCollections.FindAsync(id);
        return entity?.CreatorId;
    }

    public async Task<TrackCollectionDto?> GetPublicCollectionBySlugAsync(string slug)
    {
        var normalized = (slug ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0) return null;

        // Slug is only unique per creator, so two creators can share one. Only
        // publicly-visible albums (public | unlisted) are ever resolvable by a
        // bare slug — draft/private stay owner-only and are filtered out here,
        // so this endpoint can never surface a hidden album. Most-recent wins
        // when a slug legitimately collides across creators.
        var entity = await _db.TrackCollections.AsNoTracking()
            .Where(c => c.Slug.ToLower() == normalized
                        && (c.Visibility == AlbumVisibility.Public || c.Visibility == AlbumVisibility.Unlisted))
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();
        return entity is null ? null : MapCollectionToDto(entity);
    }

    public async Task<TrackCollectionDto> AddCollectionAsync(string creatorId, string title, string? description, string? coverImageUrl, string trackIds,
        string? visibility = null, DateTime? releaseDate = null)
    {
        var collection = new TrackCollection
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            Title = title,
            Slug = await GenerateCollectionSlugAsync(creatorId, title),
            Description = description,
            CoverImageUrl = coverImageUrl,
            TrackIds = trackIds,
            Visibility = NormalizeCollectionVisibility(visibility) ?? AlbumVisibility.Default,
            ReleaseDate = releaseDate,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.TrackCollections.Add(collection);
        SyncAlbumTracks(collection.Id, trackIds, Array.Empty<AlbumTrack>());
        await _db.SaveChangesAsync();
        return MapCollectionToDto(collection);
    }

    public async Task<TrackCollectionDto> UpdateCollectionAsync(Guid id, string creatorId, string? title, string? description, string? coverImageUrl, string? trackIds,
        string? visibility = null, DateTime? releaseDate = null, bool clearReleaseDate = false)
    {
        var existing = await _db.TrackCollections.FindAsync(id);
        if (existing is null) throw new KeyNotFoundException("Collection not found.");

        if (title is not null) existing.Title = title;
        existing.Description = description ?? existing.Description;
        if (coverImageUrl is not null) existing.CoverImageUrl = coverImageUrl;
        if (trackIds is not null)
        {
            existing.TrackIds = trackIds;
            var joinRows = await _db.AlbumTracks.Where(at => at.AlbumId == id).ToListAsync();
            SyncAlbumTracks(id, trackIds, joinRows);
        }
        var normalizedVisibility = NormalizeCollectionVisibility(visibility);
        if (normalizedVisibility is not null) existing.Visibility = normalizedVisibility;
        if (clearReleaseDate) existing.ReleaseDate = null;
        else if (releaseDate.HasValue) existing.ReleaseDate = releaseDate;
        // Slug stays stable once created (album URLs must not break on rename);
        // legacy pre-slug rows get one lazily.
        if (string.IsNullOrEmpty(existing.Slug))
            existing.Slug = await GenerateCollectionSlugAsync(existing.CreatorId, existing.Title, existing.Id);
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return MapCollectionToDto(existing);
    }

    public async Task DeleteCollectionAsync(Guid id)
    {
        var existing = await _db.TrackCollections.FindAsync(id);
        if (existing is not null)
        {
            // Deleting an album only deletes the relationship rows — the
            // tracks themselves (and their plays/likes/URLs) are untouched.
            var joinRows = await _db.AlbumTracks.Where(at => at.AlbumId == id).ToListAsync();
            _db.AlbumTracks.RemoveRange(joinRows);
            _db.TrackCollections.Remove(existing);
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Dual-write: keeps AlbumTrack join rows (canonical positions + AddedAt)
    /// in sync with the legacy CSV column. Retained tracks keep their AddedAt.
    /// Runs against the change tracker; caller saves.
    /// </summary>
    private void SyncAlbumTracks(Guid albumId, string trackIdsCsv, IReadOnlyList<AlbumTrack> existingRows)
    {
        var orderedIds = ParseCollectionTrackGuids(trackIdsCsv);
        var keep = new HashSet<Guid>(orderedIds);
        var byTrackId = new Dictionary<Guid, AlbumTrack>();
        foreach (var row in existingRows)
        {
            if (!keep.Contains(row.TrackId))
                _db.AlbumTracks.Remove(row);
            else
                byTrackId[row.TrackId] = row;
        }

        for (var position = 0; position < orderedIds.Count; position++)
        {
            if (byTrackId.TryGetValue(orderedIds[position], out var row))
            {
                row.Position = position;
            }
            else
            {
                _db.AlbumTracks.Add(new AlbumTrack
                {
                    AlbumId = albumId,
                    TrackId = orderedIds[position],
                    Position = position,
                    AddedAt = DateTime.UtcNow,
                });
            }
        }
    }

    private static List<Guid> ParseCollectionTrackGuids(string trackIdsCsv)
    {
        var result = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var piece in trackIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(piece, out var guid) && seen.Add(guid))
                result.Add(guid);
        }
        return result;
    }

    private static string? NormalizeCollectionVisibility(string? visibility) =>
        AlbumVisibility.Normalize(visibility);

    private async Task<string> GenerateCollectionSlugAsync(string creatorId, string title, Guid? excludeId = null)
    {
        var baseSlug = SlugifyCollectionTitle(title);
        var slug = baseSlug;
        var suffix = 2;
        while (await _db.TrackCollections.AnyAsync(c => c.CreatorId == creatorId && c.Slug == slug && (excludeId == null || c.Id != excludeId)))
        {
            slug = $"{baseSlug}-{suffix++}";
        }
        return slug;
    }

    private static string SlugifyCollectionTitle(string title)
    {
        var builder = new System.Text.StringBuilder(title.Length);
        var lastWasHyphen = true; // suppress leading hyphens
        foreach (var ch in title.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                builder.Append('-');
                lastWasHyphen = true;
            }
        }
        var slug = builder.ToString().TrimEnd('-');
        if (slug.Length > 200) slug = slug[..200].TrimEnd('-');
        return slug.Length > 0 ? slug : "album";
    }

    // ───── Mapping helpers ─────

    private async Task<CreatorProfileDto> MapToDtoAsync(CreatorProfile p)
    {
        List<SocialLinkDto>? links = null;
        if (!string.IsNullOrEmpty(p.SocialLinks))
        {
            try { links = JsonSerializer.Deserialize<List<SocialLinkDto>>(p.SocialLinks); }
            catch { /* ignore malformed JSON */ }
        }

        StudioSetupDto? studioSetup = null;
        if (!string.IsNullOrEmpty(p.StudioSetup))
        {
            try { studioSetup = JsonSerializer.Deserialize<StudioSetupDto>(p.StudioSetup); }
            catch { /* ignore malformed JSON */ }
        }

        List<JourneyEntryDto>? journeyEntries = null;
        if (!string.IsNullOrEmpty(p.JourneyEntries))
        {
            try { journeyEntries = JsonSerializer.Deserialize<List<JourneyEntryDto>>(p.JourneyEntries); }
            catch { /* ignore malformed JSON */ }
        }

        var stats = await ComputeStatsAsync(p.UserId);

        // Resolve canonical display name and routing username from Creator identity table
        var creator = await _db.Creators.AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == p.UserId);

        return new CreatorProfileDto
        {
            Id = p.Id.ToString(),
            UserId = p.UserId,
            Slug = p.Slug,
            DisplayName = creator?.DisplayName,
            Username = creator?.Username,
            Bio = p.Bio,
            Niche = p.Niche,
            BannerImageUrl = p.BannerImageUrl,
            ProfileImageUrl = p.ProfileImageUrl,
            SocialLinks = links,
            StudioSetup = studioSetup,
            JourneyEntries = journeyEntries,
            ShowEarnings = p.ShowEarnings,
            ShowDownloadStats = p.ShowDownloadStats,
            PinnedTrackIds = p.PinnedTrackIds,
            Stats = stats,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
        };
    }

    private async Task<CreatorStatsDto> ComputeStatsAsync(string userId)
    {
        // Resolve UUID-based creator identity (if exists) for dual-FK track lookup
        var creatorUuid = await _db.Creators.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync();

        var totalSales = await _db.Purchases
            .CountAsync(p => _db.Tracks.Any(t =>
                    (t.CreatorId == userId || (creatorUuid != null && t.CreatorUuid == creatorUuid))
                    && t.Id == p.TrackId)
                             && p.Status == "completed");

        // Lifetime plays across all of the creator's tracks (StreamSessions).
        var totalPlays = await _db.StreamSessions
            .CountAsync(s => _db.Tracks.Any(t =>
                (t.CreatorId == userId || (creatorUuid != null && t.CreatorUuid == creatorUuid))
                && t.Id == s.TrackId));

        // Follower count — CreatorFollows keyed by the canonical Creator UUID.
        var followerCount = creatorUuid != null
            ? await _db.CreatorFollows.CountAsync(f => f.CreatorId == creatorUuid)
            : 0;

        // F18: creator earnings are intentionally NOT included in this stats DTO —
        // it is serialized on the anonymous storefront/profile routes. The withdrawable
        // balance is owner-only via the authenticated wallet (PayoutService.GetEarningsAsync).
        return new CreatorStatsDto
        {
            TotalDownloads = totalSales,
            TotalPlays = totalPlays,
            FollowerCount = followerCount,
        };
    }

    private static TrackCollectionDto MapCollectionToDto(TrackCollection c) => new()
    {
        Id = c.Id.ToString(),
        Title = c.Title,
        Slug = c.Slug,
        Description = c.Description,
        CoverImageUrl = c.CoverImageUrl,
        TrackIds = string.IsNullOrWhiteSpace(c.TrackIds)
            ? Array.Empty<string>()
            : c.TrackIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        // Fail-closed read normalization: empty falls back to the historical
        // default (public); the legacy "hidden" maps to "private"; any
        // unrecognized value collapses to "private" so it can never leak.
        Visibility = string.IsNullOrWhiteSpace(c.Visibility)
            ? AlbumVisibility.Public
            : (AlbumVisibility.Normalize(c.Visibility) ?? AlbumVisibility.Private),
        ReleaseDate = c.ReleaseDate,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
    };
}
