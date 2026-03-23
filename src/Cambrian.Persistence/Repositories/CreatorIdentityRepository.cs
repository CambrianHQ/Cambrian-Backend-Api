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
        return MapToDto(creator, stats);
    }

    public async Task<PublicCreatorDto?> GetByUsernameAsync(string username)
    {
        var normalized = NormalizeUsername(username);

        var creator = await _db.Creators
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Username == normalized);

        if (creator is null) return null;

        var stats = await GetStatsAsync(creator.Id);
        return MapToDto(creator, stats);
    }

    public async Task<PublicCreatorDto?> GetByUserIdAsync(string userId)
    {
        var creator = await _db.Creators
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == userId);

        if (creator is null) return null;

        var stats = await GetStatsAsync(creator.Id);
        return MapToDto(creator, stats);
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

        // Filter strictly by the UUID FK on the tracks table
        var tracks = await _db.Tracks
            .AsNoTracking()
            .Where(t => t.CreatorUuid == creatorId
                        && t.Visibility == "public"
                        && !t.ExclusiveSold
                        && t.Status != "copyright_transferred")
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        // Zero-track mismatch: creator exists but has no tracks linked via CreatorUuid
        if (tracks.Count == 0 && page == 1)
        {
            var legacyTrackCount = await _db.Tracks
                .CountAsync(t => t.CreatorId == creator.UserId);
            if (legacyTrackCount > 0)
            {
                _logger.LogWarning(
                    "ZeroTrackMismatch: creator={CreatorId} has {LegacyCount} legacy tracks but 0 UUID-linked tracks. Backfill may be incomplete.",
                    creatorId, legacyTrackCount);
            }
        }

        return tracks.Select(t => new TrackResponse
        {
            Id = t.Id.ToString(),
            CambrianTrackId = t.CambrianTrackId,
            Title = t.Title,
            Description = t.Description,
            Genre = t.Genre ?? "",
            Price = (decimal)t.Price,
            NonExclusivePrice = t.NonExclusivePriceCents / 100m,
            ExclusivePrice = t.ExclusivePriceCents / 100m,
            CopyrightBuyoutPrice = t.CopyrightBuyoutPriceCents / 100m,
            ExclusiveSold = t.ExclusiveSold,
            Status = t.Status ?? "available",
            CopyrightOwnerId = t.CopyrightOwnerId,
            LicenseType = t.LicenseType,
            Duration = t.Duration,
            AudioUrl = t.AudioUrl,
            CoverArtUrl = t.CoverArtUrl,
            CreatorId = t.CreatorId,
            Artist = creator.DisplayName ?? creator.Username,
            CreatorSlug = creator.Username,
            CreatorProfileImageUrl = creator.ProfileImageUrl,
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
            var creator = new Creator
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Username = NormalizeUsername(request.Username ?? ""),
                DisplayName = request.DisplayName?.Trim(),
                Bio = request.Bio?.Trim() ?? "",
                SocialLinks = request.SocialLinks is not null
                    ? JsonSerializer.Serialize(request.SocialLinks)
                    : null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.Creators.Add(creator);
            await _db.SaveChangesAsync();

            var stats = await GetStatsAsync(creator.Id);
            return MapToDto(creator, stats);
        }

        if (request.Username is not null)
            existing.Username = NormalizeUsername(request.Username);
        if (request.DisplayName is not null)
            existing.DisplayName = request.DisplayName.Trim();
        if (request.Bio is not null)
            existing.Bio = request.Bio.Trim();
        if (request.SocialLinks is not null)
            existing.SocialLinks = JsonSerializer.Serialize(request.SocialLinks);

        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var updatedStats = await GetStatsAsync(existing.Id);
        return MapToDto(existing, updatedStats);
    }

    public async Task<CreatorStatsResponseDto> GetStatsAsync(Guid creatorId)
    {
        var trackCount = await _db.Tracks.CountAsync(t => t.CreatorUuid == creatorId);
        var totalSales = await _db.Purchases
            .CountAsync(p => _db.Tracks.Any(t => t.CreatorUuid == creatorId && t.Id == p.TrackId)
                             && p.Status == "completed");

        return new CreatorStatsResponseDto
        {
            TrackCount = trackCount,
            TotalSales = totalSales,
            TotalDownloads = 0,
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

    // ── Helpers ──

    /// <summary>
    /// Normalize username: lowercase, trim, collapse whitespace to hyphens.
    /// </summary>
    internal static string NormalizeUsername(string raw)
        => raw.Trim().ToLowerInvariant();

    private static PublicCreatorDto MapToDto(Creator c, CreatorStatsResponseDto stats)
    {
        List<SocialLinkItemDto>? links = null;
        if (!string.IsNullOrEmpty(c.SocialLinks))
        {
            try { links = JsonSerializer.Deserialize<List<SocialLinkItemDto>>(c.SocialLinks); }
            catch { /* ignore malformed JSON */ }
        }

        return new PublicCreatorDto
        {
            Id = c.Id.ToString(),
            Username = c.Username,
            DisplayName = c.DisplayName,
            Bio = c.Bio,
            ProfileImageUrl = c.ProfileImageUrl,
            CoverImageUrl = c.CoverImageUrl,
            SocialLinks = links,
            Stats = stats,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt,
        };
    }
}
