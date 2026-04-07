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

        // Dual-FK query: match CreatorUuid (new) OR CreatorId (legacy string FK)
        var legacyUserId = creator.UserId;
        var tracks = await _db.Tracks
            .AsNoTracking()
            .Where(t => (t.CreatorUuid == creatorId || t.CreatorId == legacyUserId)
                        && t.Visibility == "public"
                        && !t.ExclusiveSold
                        && t.Status != "copyright_transferred")
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return tracks.Select(t => new TrackResponse
        {
            Id = t.Id.ToString(),
            CambrianTrackId = t.CambrianTrackId,
            Title = t.Title,
            Description = t.Description,
            Genre = t.Genre ?? "",
            Price = (decimal)t.Price,
            NonExclusivePrice = t.NonExclusivePriceCents > 0 ? t.NonExclusivePriceCents / 100m : (decimal)t.Price,
            ExclusivePrice = t.ExclusivePriceCents > 0 ? t.ExclusivePriceCents / 100m : (decimal)t.Price,
            CopyrightBuyoutPrice = t.CopyrightBuyoutPriceCents > 0
                ? t.CopyrightBuyoutPriceCents / 100m
                : (t.ExclusivePriceCents > 0 ? t.ExclusivePriceCents / 100m : (decimal)t.Price),
            ExclusiveSold = t.ExclusiveSold,
            Status = t.Status ?? "available",
            IsCopyrightTransferred = t.CopyrightOwnerId != null,
            LicenseType = t.LicenseType,
            Duration = t.Duration,
            AudioUrl = t.AudioUrl,
            CoverArtUrl = t.CoverArtUrl,
            CreatorId = t.CreatorId,
            Artist = creator.DisplayName ?? creator.Username,
            CreatorSlug = creator.Username,
            CreatorProfileImageUrl = profileImageUrl,
            CreatedAt = t.CreatedAt,
        }).ToList();
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

        return new CreatorStatsResponseDto
        {
            TrackCount = trackCount,
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
        // Canonical source for presentation fields
        var profile = await _db.CreatorProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == c.UserId);

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
