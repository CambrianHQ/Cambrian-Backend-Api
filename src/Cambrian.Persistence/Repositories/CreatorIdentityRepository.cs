using System.Text.Json;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.Creators;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cambrian.Persistence.Repositories;

/// <summary>
/// Repository for the Creators table.
/// All relational queries use creator.Id (UUID) — never email or username.
/// </summary>
public sealed class CreatorIdentityRepository : ICreatorIdentityRepository
{
    private readonly CambrianDbContext _db;
    private readonly ILogger<CreatorIdentityRepository> _logger;

    public CreatorIdentityRepository(CambrianDbContext db, ILogger<CreatorIdentityRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<PublicCreatorDto?> GetByIdAsync(Guid creatorId)
    {
        var creator = await _db.Creators
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == creatorId);

        if (creator is null) return null;

        var stats = await GetStatsAsync(creatorId);
        var tracks = await GetTracksByCreatorIdAsync(creatorId, 1, 50);
        return await MapToDtoAsync(creator, stats, tracks);
    }

    public async Task<PublicCreatorDto?> GetByUsernameAsync(string username)
    {
        var normalized = NormalizeUsername(username);

        var creator = await _db.Creators
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Username == normalized);

        if (creator is null) return null;

        var stats = await GetStatsAsync(creator.Id);
        var tracks = await GetTracksByCreatorIdAsync(creator.Id, 1, 50);
        return await MapToDtoAsync(creator, stats, tracks);
    }

    public async Task<PublicCreatorDto?> GetByUserIdAsync(string userId)
    {
        var creator = await _db.Creators
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == userId);

        if (creator is null) return null;

        var stats = await GetStatsAsync(creator.Id);
        var tracks = await GetTracksByCreatorIdAsync(creator.Id, 1, 50);
        return await MapToDtoAsync(creator, stats, tracks);
    }

    public async Task<IReadOnlyList<CreatorSearchResultDto>> SearchAsync(string query, int limit)
    {
        var q = query?.Trim().ToLowerInvariant() ?? "";
        if (q.Length == 0) return Array.Empty<CreatorSearchResultDto>();
        limit = Math.Clamp(limit, 1, 50);

        // Lowercase both sides so the match is case-insensitive on both Postgres (LIKE is
        // case-sensitive) and SQLite. Username is already normalized lowercase; DisplayName is not.
        var matches = await _db.Creators.AsNoTracking()
            .Where(c => c.Username.ToLower().Contains(q)
                        || (c.DisplayName != null && c.DisplayName.ToLower().Contains(q)))
            .OrderBy(c => c.Username)
            .Take(limit)
            .Select(c => new { c.Id, c.UserId, c.Username, c.DisplayName, c.ProfileImageUrl })
            .ToListAsync();

        if (matches.Count == 0) return Array.Empty<CreatorSearchResultDto>();

        // Canonical presentation fields live on CreatorProfile — batch-load them.
        var userIds = matches.Select(m => m.UserId).ToList();
        var profiles = await _db.CreatorProfiles.AsNoTracking()
            .Where(p => userIds.Contains(p.UserId))
            .Select(p => new { p.UserId, p.Slug, p.ProfileImageUrl, p.Bio })
            .ToListAsync();
        var profileByUser = profiles.ToDictionary(p => p.UserId);

        var results = new List<CreatorSearchResultDto>(matches.Count);
        foreach (var m in matches)
        {
            profileByUser.TryGetValue(m.UserId, out var prof);
            // Dual-FK count (UUID FK or legacy string FK), public only. Bounded by the result cap.
            var trackCount = await _db.Tracks.CountAsync(t =>
                (t.CreatorUuid == m.Id || t.CreatorId == m.UserId)
                && t.Visibility == "public"
                && !t.ExclusiveSold
                && t.Status != "copyright_transferred"
                && t.Status != "removed");

            results.Add(new CreatorSearchResultDto
            {
                Id = m.Id.ToString(),
                Username = m.Username,
                DisplayName = m.DisplayName,
                // Slug is the profile-page handle; guarantee it is never empty so
                // clients can always build a /@{slug} link (falls back to username).
                Slug = string.IsNullOrWhiteSpace(prof?.Slug) ? m.Username : prof!.Slug,
                ProfileImageUrl = prof?.ProfileImageUrl ?? m.ProfileImageUrl,
                Bio = prof?.Bio ?? "",
                TrackCount = trackCount,
            });
        }

        return results;
    }

    /// <summary>
    /// Critical query: filter tracks WHERE t.CreatorId == creatorId (UUID FK).
    /// Join creator once for creatorUsername and creatorDisplayName.
    /// Must NOT use email. Must NOT use username as filter.
    /// </summary>
    public async Task<List<TrackResponse>> GetTracksByCreatorIdAsync(Guid creatorId, int page, int pageSize)
    {
        // Guard: reject empty/default creator ID
        if (creatorId == Guid.Empty)
            throw new ArgumentException("creatorId must be a valid UUID.", nameof(creatorId));

        var creator = await _db.Creators
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == creatorId);

        if (creator is null)
            return new List<TrackResponse>();

        // Canonical source for profile image: CreatorProfile
        var profileImageUrl = await _db.CreatorProfiles.AsNoTracking()
            .Where(p => p.UserId == creator.UserId)
            .Select(p => p.ProfileImageUrl)
            .FirstOrDefaultAsync();

        var artistName = creator.DisplayName ?? creator.Username;
        // Dual-FK query: match CreatorUuid (new) OR CreatorId (legacy string FK)
        var legacyUserId = creator.UserId;
        // Project directly to the API DTO so stale databases missing newer Track columns
        // like PrimaryGenre/Subgenre do not fail this storefront query.
        var items = await _db.Tracks
            .AsNoTracking()
            .Where(t => (t.CreatorUuid == creatorId || t.CreatorId == legacyUserId)
                        && t.Visibility == "public"
                        && !t.ExclusiveSold
                        && t.Status != "copyright_transferred"
                        && t.Status != "removed")
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TrackResponse
            {
                Id = t.Id.ToString(),
                CambrianTrackId = t.CambrianTrackId,
                Title = t.Title,
                Description = t.Description,
                Genre = t.Genre ?? "",
                Price = t.NonExclusivePriceCents > 0 ? t.NonExclusivePriceCents / 100m : t.Price,
                NonExclusivePrice = t.NonExclusivePriceCents > 0 ? t.NonExclusivePriceCents / 100m : t.Price,
                Status = t.Status == "exclusive_sold" || t.Status == "copyright_transferred" ? "available" : (t.Status ?? "available"),
                Duration = t.Duration,
                AudioUrl = t.AudioUrl,
                CoverArtUrl = t.CoverArtUrl,
                CreatorId = t.CreatorId,
                Artist = artistName,
                CreatorSlug = creator.Username,
                CreatorProfileImageUrl = profileImageUrl,
                CreatedAt = t.CreatedAt,
            })
            .ToListAsync();

        await PopulateEngagementAsync(items);
        return items;
    }

    /// <summary>
    /// Paged variant: returns items + total row count so callers can emit a
    /// pagination envelope. Uses the same visibility filters as
    /// GetTracksByCreatorIdAsync so the storefront surface is consistent.
    /// </summary>
    public async Task<PagedResult<TrackResponse>> GetTracksPagedByCreatorIdAsync(Guid creatorId, int page, int pageSize)
    {
        if (creatorId == Guid.Empty)
            throw new ArgumentException("creatorId must be a valid UUID.", nameof(creatorId));

        var creator = await _db.Creators
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == creatorId);

        if (creator is null)
        {
            return new PagedResult<TrackResponse>
            {
                Items = Array.Empty<TrackResponse>(),
                Page = page,
                PageSize = pageSize,
                TotalCount = 0,
            };
        }

        var profileImageUrl = await _db.CreatorProfiles.AsNoTracking()
            .Where(p => p.UserId == creator.UserId)
            .Select(p => p.ProfileImageUrl)
            .FirstOrDefaultAsync();

        var artistName = creator.DisplayName ?? creator.Username;
        var legacyUserId = creator.UserId;

        var baseQuery = _db.Tracks
            .AsNoTracking()
            .Where(t => (t.CreatorUuid == creatorId || t.CreatorId == legacyUserId)
                        && t.Visibility == "public"
                        && !t.ExclusiveSold
                        && t.Status != "copyright_transferred"
                        && t.Status != "removed");

        var total = await baseQuery.CountAsync();

        var items = await baseQuery
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TrackResponse
            {
                Id = t.Id.ToString(),
                CambrianTrackId = t.CambrianTrackId,
                Title = t.Title,
                Description = t.Description,
                Genre = t.Genre ?? "",
                Price = t.NonExclusivePriceCents > 0 ? t.NonExclusivePriceCents / 100m : t.Price,
                NonExclusivePrice = t.NonExclusivePriceCents > 0 ? t.NonExclusivePriceCents / 100m : t.Price,
                Status = t.Status == "exclusive_sold" || t.Status == "copyright_transferred" ? "available" : (t.Status ?? "available"),
                Duration = t.Duration,
                AudioUrl = t.AudioUrl,
                CoverArtUrl = t.CoverArtUrl,
                CreatorId = t.CreatorId,
                Artist = artistName,
                CreatorSlug = creator.Username,
                CreatorProfileImageUrl = profileImageUrl,
                CreatedAt = t.CreatedAt,
            })
            .ToListAsync();

        await PopulateEngagementAsync(items);

        return new PagedResult<TrackResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
        };
    }

    /// <summary>
    /// Populate per-track engagement (plays, sales) on storefront track DTOs.
    /// Mirrors the catalog path (TrackRepository.GetTrackStatsAsync): qualified plays
    /// come from TrackStats; sales = COUNT(completed Purchases). Without this the
    /// public creator profile shows zero plays even when the catalog shows real
    /// counts (the projection above sets neither field, so both default to 0).
    /// </summary>
    private async Task PopulateEngagementAsync(List<TrackResponse> items)
    {
        if (items.Count == 0) return;

        var ids = items
            .Select(i => Guid.TryParse(i.Id, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();
        if (ids.Count == 0) return;

        Dictionary<Guid, long> plays;
        try
        {
            plays = await _db.TrackStats
                .AsNoTracking()
                .Where(s => ids.Contains(s.TrackId))
                .ToDictionaryAsync(s => s.TrackId, s => s.PlayCount);
        }
        catch (Exception ex) when (IsMissingTrackStatsTable(ex))
        {
            // Some supported legacy schemas predate TrackStats. They have no
            // authoritative qualified-play projection, so fail closed to zero
            // instead of resurrecting raw StreamSession counts.
            _logger.LogWarning("TrackStats is unavailable on a legacy schema; storefront play counts default to zero.");
            plays = new Dictionary<Guid, long>();
        }

        var sales = await _db.Purchases
            .Where(p => ids.Contains(p.TrackId) && p.Status == "completed")
            .GroupBy(p => p.TrackId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        // Issued Human Authorship Record id per track (null until one is issued);
        // dedupe defensively by most recently issued.
        var authorshipRows = await _db.AuthorshipRecords
            .Where(a => ids.Contains(a.TrackId) && a.Status == "issued")
            .Select(a => new { a.TrackId, a.Id, a.IssuedAt })
            .ToListAsync();
        var authorship = authorshipRows
            .GroupBy(a => a.TrackId)
            .ToDictionary(grp => grp.Key, grp => grp.OrderByDescending(a => a.IssuedAt).First().Id);

        foreach (var it in items)
        {
            if (Guid.TryParse(it.Id, out var g))
            {
                it.Plays = plays.GetValueOrDefault(g);
                it.Sales = sales.GetValueOrDefault(g);
                if (authorship.TryGetValue(g, out var recId))
                    it.AuthorshipRecordId = recId.ToString();
            }
        }
    }

    public async Task<bool> IsUsernameTakenAsync(string normalizedUsername, Guid? excludeCreatorId = null)
    {
        var query = _db.Creators.AsNoTracking()
            .Where(c => c.Username == normalizedUsername);

        if (excludeCreatorId.HasValue)
            query = query.Where(c => c.Id != excludeCreatorId.Value);

        var taken = await query.AnyAsync();
        if (taken)
        {
            _logger.LogInformation("DuplicateUsername: username={Username} already taken (excludeId={ExcludeId})",
                normalizedUsername, excludeCreatorId);
        }
        return taken;
    }

    public async Task<PublicCreatorDto> UpsertAsync(string userId, UpdateCreatorProfileRequest request)
    {
        var existing = await _db.Creators.FirstOrDefaultAsync(c => c.UserId == userId);

        if (existing is null)
        {
            if (string.IsNullOrWhiteSpace(request.Username))
                throw new ArgumentException("Username is required when creating a creator profile.");

            var normalizedUsername = NormalizeUsername(request.Username ?? "");
            var creator = new Creator
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Username = normalizedUsername,
                DisplayName = request.DisplayName?.Trim() ?? normalizedUsername,
                Bio = "",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.Creators.Add(creator);
            await _db.SaveChangesAsync();

            var stats = await GetStatsAsync(creator.Id);
            return await MapToDtoAsync(creator, stats);
        }

        // Username is immutable once set — only apply on first assignment.
        if (request.Username is not null && string.IsNullOrWhiteSpace(existing.Username))
        {
            var normalized = NormalizeUsername(request.Username);
            existing.Username = normalized;
            existing.DisplayName = normalized;
        }
        if (request.DisplayName is not null)
            existing.DisplayName = request.DisplayName.Trim();

        // Sync SocialLinks to Creator (legacy fallback) when provided
        if (request.SocialLinks is not null)
            existing.SocialLinks = System.Text.Json.JsonSerializer.Serialize(request.SocialLinks);

        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var updatedStats = await GetStatsAsync(existing.Id);
        return await MapToDtoAsync(existing, updatedStats);
    }

    public async Task<CreatorStatsResponseDto> GetStatsAsync(Guid creatorId)
    {
        // Resolve legacy userId for dual-FK track lookup
        var legacyUserId = await _db.Creators.AsNoTracking()
            .Where(c => c.Id == creatorId)
            .Select(c => c.UserId)
            .FirstOrDefaultAsync();

        var trackCount = await _db.Tracks.CountAsync(t =>
            t.CreatorUuid == creatorId || (legacyUserId != null && t.CreatorId == legacyUserId));
        var totalSales = await _db.Purchases
            .CountAsync(p => _db.Tracks.Any(t =>
                    (t.CreatorUuid == creatorId || (legacyUserId != null && t.CreatorId == legacyUserId))
                    && t.Id == p.TrackId)
                             && p.Status == "completed");
        long totalPlays;
        try
        {
            totalPlays = await _db.TrackStats
                .AsNoTracking()
                .Where(s => _db.Tracks.Any(t =>
                    (t.CreatorUuid == creatorId || (legacyUserId != null && t.CreatorId == legacyUserId))
                    && t.Id == s.TrackId))
                .SumAsync(s => (long?)s.PlayCount) ?? 0L;
        }
        catch (Exception ex) when (IsMissingTrackStatsTable(ex))
        {
            _logger.LogWarning("TrackStats is unavailable on a legacy schema; creator play totals default to zero.");
            totalPlays = 0L;
        }

        return new CreatorStatsResponseDto
        {
            TrackCount = trackCount,
            TotalPlays = totalPlays,
            TotalSales = totalSales,
            TotalDownloads = totalSales,
            AverageRating = 0,
            FollowerCount = 0,
        };
    }

    public async Task<Guid?> GetCreatorIdForUserAsync(string userId)
    {
        return await _db.Creators
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<PublicCreatorDto?> ResolveByLegacyIdentifierAsync(string identifier)
    {
        // 1. Try as UUID
        if (Guid.TryParse(identifier, out var guid))
        {
            var byId = await GetByIdAsync(guid);
            if (byId is not null) return byId;
        }

        // 2. Try as ApplicationUser.Id (legacy string FK)
        var byUserId = await GetByUserIdAsync(identifier);
        if (byUserId is not null)
        {
            _logger.LogInformation("LegacyResolve: resolved userId={UserId} to creator={CreatorId}",
                identifier, byUserId.Id);
            return byUserId;
        }

        // 3. Try as username
        var byUsername = await GetByUsernameAsync(identifier);
        if (byUsername is not null)
        {
            _logger.LogInformation("LegacyResolve: resolved username={Username} to creator={CreatorId}",
                identifier, byUsername.Id);
            return byUsername;
        }

        // 4. Try as CreatorProfile slug for cases where the public route is configured
        // before the creator identity row has the routable username mirrored across.
        var profileUserId = await _db.CreatorProfiles
            .AsNoTracking()
            .Where(p => p.Slug.ToLower() == identifier.Trim().ToLower())
            .Select(p => p.UserId)
            .FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace(profileUserId))
        {
            var byProfileSlug = await GetByUserIdAsync(profileUserId);
            if (byProfileSlug is not null)
            {
                _logger.LogInformation("LegacyResolve: resolved profile slug={Slug} to creator={CreatorId}",
                    identifier, byProfileSlug.Id);
                return byProfileSlug;
            }
        }

        // 5. Try as ApplicationUser.UserName — handles creators who set their Identity
        //    username before the Creator table existed (migration 18) and never got a
        //    Creator row auto-provisioned.
        var normalized = identifier.Trim().ToLowerInvariant();
        var legacyUser = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.NormalizedUserName == normalized.ToUpperInvariant()
                                      && u.Role == "Creator");
        if (legacyUser is not null)
        {
            _logger.LogInformation(
                "LegacyResolve: backfilling Creator row for userId={UserId} username={Username}",
                legacyUser.Id, normalized);

            // Auto-provision the missing Creator row so future lookups hit step 3 directly.
            var backfilled = await UpsertAsync(legacyUser.Id,
                new Application.DTOs.Creators.UpdateCreatorProfileRequest
                {
                    Username = normalized,
                    DisplayName = legacyUser.DisplayName ?? normalized
                });
            return backfilled;
        }

        _logger.LogWarning("LegacyResolve: unresolved identifier={Identifier}", identifier);
        return null;
    }

    public async Task FollowAsync(string followerUserId, Guid creatorId)
    {
        var exists = await _db.CreatorFollows
            .AnyAsync(f => f.FollowerId == followerUserId && f.CreatorId == creatorId);
        if (exists) return;

        _db.CreatorFollows.Add(new CreatorFollow
        {
            FollowerId = followerUserId,
            CreatorId = creatorId,
        });
        await _db.SaveChangesAsync();
    }

    public async Task UnfollowAsync(string followerUserId, Guid creatorId)
    {
        var follow = await _db.CreatorFollows
            .FirstOrDefaultAsync(f => f.FollowerId == followerUserId && f.CreatorId == creatorId);
        if (follow is null) return;

        _db.CreatorFollows.Remove(follow);
        await _db.SaveChangesAsync();
    }

    public Task<bool> IsFollowingAsync(string followerUserId, Guid creatorId)
        => _db.CreatorFollows
            .AnyAsync(f => f.FollowerId == followerUserId && f.CreatorId == creatorId);

    public Task<int> GetFollowerCountAsync(Guid creatorId)
        => _db.CreatorFollows.CountAsync(f => f.CreatorId == creatorId);

    // ── Helpers ──

    private static bool IsMissingTrackStatsTable(Exception ex)
    {
        var message = ex.ToString();
        var missingRelation =
            message.Contains("no such table", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid object name", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("undefined table", StringComparison.OrdinalIgnoreCase);

        return missingRelation && message.Contains("TrackStats", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalize username: lowercase, trim, collapse whitespace to hyphens.
    /// </summary>
    internal static string NormalizeUsername(string raw)
        => raw.Trim().ToLowerInvariant();

    /// <summary>
    /// Build PublicCreatorDto with canonical presentation fields from CreatorProfile.
    /// Creator table only provides identity fields (Username, DisplayName).
    /// </summary>
    private async Task<PublicCreatorDto> MapToDtoAsync(Creator c, CreatorStatsResponseDto stats, List<TrackResponse>? tracks = null)
    {
        // Canonical source for presentation fields.
        // Project only the fields we need so this resolver remains compatible with
        // older CreatorProfiles schemas that may not yet have newer toggle columns.
        var profile = await _db.CreatorProfiles.AsNoTracking()
            .Where(p => p.UserId == c.UserId)
            .Select(p => new
            {
                p.Bio,
                p.ProfileImageUrl,
                p.BannerImageUrl,
                p.SocialLinks
            })
            .FirstOrDefaultAsync();

        var socialLinksSource = profile?.SocialLinks;
        List<SocialLinkItemDto>? links = null;
        if (!string.IsNullOrEmpty(socialLinksSource))
        {
            try { links = JsonSerializer.Deserialize<List<SocialLinkItemDto>>(socialLinksSource); }
            catch { /* ignore malformed JSON */ }
        }

        return new PublicCreatorDto
        {
            Id = c.Id.ToString(),
            UserId = c.UserId,
            Username = c.Username,
            DisplayName = c.DisplayName,
            Bio = profile?.Bio ?? "",
            ProfileImageUrl = profile?.ProfileImageUrl,
            CoverImageUrl = profile?.BannerImageUrl,
            SocialLinks = links,
            Stats = stats,
            Tracks = tracks ?? new(),
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt,
        };
    }
}
