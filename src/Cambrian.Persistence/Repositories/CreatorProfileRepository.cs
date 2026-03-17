using System.Text.Json;
using Cambrian.Application.DTOs.CreatorProfile;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public sealed class CreatorProfileRepository : ICreatorProfileRepository
{
    private readonly CambrianDbContext _db;

    public CreatorProfileRepository(CambrianDbContext db) => _db = db;

    public async Task<CreatorProfileDto?> GetByUserIdAsync(string userId)
    {
        var profiles = await _db.CreatorProfiles.AsNoTracking().ToListAsync();
        foreach (var p in profiles)
        {
            if (p.UserId == userId) return MapToDto(p);
        }
        return null;
    }

    public async Task<CreatorProfileDto?> GetBySlugAsync(string slug)
    {
        var profiles = await _db.CreatorProfiles.AsNoTracking().ToListAsync();
        foreach (var p in profiles)
        {
            if (string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase)) return MapToDto(p);
        }
        return null;
    }

    public async Task<CreatorProfileDto> UpsertAsync(string userId, string slug, string bio, string? niche,
        string? socialLinksJson, bool showEarnings, bool showDownloadStats,
        string? bannerImageUrl, string? profileImageUrl)
    {
        CreatorProfile? existing = null;
        var all = await _db.CreatorProfiles.ToListAsync();
        foreach (var p in all)
        {
            if (p.UserId == userId) { existing = p; break; }
        }

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
                ShowEarnings = showEarnings,
                ShowDownloadStats = showDownloadStats,
                BannerImageUrl = bannerImageUrl,
                ProfileImageUrl = profileImageUrl,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.CreatorProfiles.Add(profile);
            await _db.SaveChangesAsync();
            return MapToDto(profile);
        }

        existing.Slug = slug;
        existing.Bio = bio;
        existing.Niche = niche;
        existing.SocialLinks = socialLinksJson ?? existing.SocialLinks;
        existing.ShowEarnings = showEarnings;
        existing.ShowDownloadStats = showDownloadStats;
        if (bannerImageUrl is not null) existing.BannerImageUrl = bannerImageUrl;
        if (profileImageUrl is not null) existing.ProfileImageUrl = profileImageUrl;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return MapToDto(existing);
    }

    public async Task<CreatorProfileDto> UpdateImageAsync(string userId, string? bannerImageUrl, string? profileImageUrl)
    {
        CreatorProfile? existing = null;
        var all = await _db.CreatorProfiles.ToListAsync();
        foreach (var p in all)
        {
            if (p.UserId == userId) { existing = p; break; }
        }
        if (existing is null) throw new KeyNotFoundException("Profile not found.");

        if (bannerImageUrl is not null) existing.BannerImageUrl = bannerImageUrl;
        if (profileImageUrl is not null) existing.ProfileImageUrl = profileImageUrl;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return MapToDto(existing);
    }

    public async Task<IReadOnlyList<TrackCollectionDto>> GetCollectionsAsync(string creatorId)
    {
        var all = await _db.TrackCollections.AsNoTracking().ToListAsync();
        var result = new List<TrackCollectionDto>();
        foreach (var c in all)
        {
            if (c.CreatorId == creatorId) result.Add(MapCollectionToDto(c));
        }
        return result;
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

    public async Task<TrackCollectionDto> AddCollectionAsync(string creatorId, string title, string? description, string trackIds)
    {
        var collection = new TrackCollection
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            Title = title,
            Description = description,
            TrackIds = trackIds,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.TrackCollections.Add(collection);
        await _db.SaveChangesAsync();
        return MapCollectionToDto(collection);
    }

    public async Task<TrackCollectionDto> UpdateCollectionAsync(Guid id, string creatorId, string? title, string? description, string? trackIds)
    {
        var existing = await _db.TrackCollections.FindAsync(id);
        if (existing is null) throw new KeyNotFoundException("Collection not found.");

        if (title is not null) existing.Title = title;
        existing.Description = description ?? existing.Description;
        if (trackIds is not null) existing.TrackIds = trackIds;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return MapCollectionToDto(existing);
    }

    public async Task DeleteCollectionAsync(Guid id)
    {
        var existing = await _db.TrackCollections.FindAsync(id);
        if (existing is not null)
        {
            _db.TrackCollections.Remove(existing);
            await _db.SaveChangesAsync();
        }
    }

    // ───── Mapping helpers ─────

    private static CreatorProfileDto MapToDto(CreatorProfile p)
    {
        List<SocialLinkDto>? links = null;
        if (!string.IsNullOrEmpty(p.SocialLinks))
        {
            try { links = JsonSerializer.Deserialize<List<SocialLinkDto>>(p.SocialLinks); }
            catch { /* ignore malformed JSON */ }
        }

        return new CreatorProfileDto
        {
            Id = p.Id.ToString(),
            UserId = p.UserId,
            Slug = p.Slug,
            Bio = p.Bio,
            Niche = p.Niche,
            BannerImageUrl = p.BannerImageUrl,
            ProfileImageUrl = p.ProfileImageUrl,
            SocialLinks = links,
            Stats = new CreatorStatsDto
            {
                TotalDownloads = 0,
                TotalEarnings = 0,
            },
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
        };
    }

    private static TrackCollectionDto MapCollectionToDto(TrackCollection c) => new()
    {
        Id = c.Id.ToString(),
        Title = c.Title,
        Description = c.Description,
        CoverImageUrl = c.CoverImageUrl,
        TrackIds = c.TrackIds,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
    };
}
