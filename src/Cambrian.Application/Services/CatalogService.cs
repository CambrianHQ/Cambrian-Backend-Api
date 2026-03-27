using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

public class CatalogService : ICatalogService
{
    private readonly ITrackRepository _tracks;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ICreatorProfileRepository _profiles;
    private readonly ILogger<CatalogService> _logger;

    public CatalogService(ITrackRepository tracks, UserManager<ApplicationUser> users, ICreatorProfileRepository profiles, ILogger<CatalogService> logger)
    {
        _tracks = tracks;
        _users = users;
        _profiles = profiles;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<TrackResponse>> GetCatalogAsync(int page = 1, int pageSize = 50, string? genre = null, string? search = null, string? sort = null)
    {
        return await GetCatalogAsync(page, pageSize, genre, search, sort, null, null, null, null);
    }

    public async Task<IReadOnlyCollection<TrackResponse>> GetCatalogAsync(int page, int pageSize, string? genre, string? search, string? sort,
        string? mood, string? tempo, bool? instrumental, string? duration)
    {
        var tracks = await _tracks.BrowseAsync(page, pageSize, genre, search, sort, mood, tempo, instrumental, duration);
        _logger.LogInformation("Catalog query: page={Page} size={Size} genre={Genre} search={Search} results={Count}", page, pageSize, genre, search, tracks.Count);
        return await MapBatchAsync(tracks);
    }

    public async Task<IReadOnlyCollection<TrackResponse>> GetDiscoverAsync(int page = 1, int pageSize = 20, string? genre = null, string? search = null)
    {
        return await GetCatalogAsync(page, pageSize, genre, search);
    }

    public async Task<IReadOnlyCollection<TrackResponse>> GetDiscoverAsync(int page, int pageSize, string? genre, string? search,
        string? mood, string? tempo, bool? instrumental, string? duration)
    {
        return await GetCatalogAsync(page, pageSize, genre, search, null, mood, tempo, instrumental, duration);
    }

    public async Task<PagedResult<TrackResponse>> GetCatalogPagedAsync(int page, int pageSize, string? genre, string? search, string? sort,
        string? mood, string? tempo, bool? instrumental, string? duration)
    {
        var totalCount = await _tracks.CountAsync(genre, search, mood, tempo, instrumental, duration);
        var tracks = await _tracks.BrowseAsync(page, pageSize, genre, search, sort, mood, tempo, instrumental, duration);
        var items = await MapBatchAsync(tracks);

        return new PagedResult<TrackResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<PagedResult<TrackResponse>> GetDiscoverPagedAsync(int page, int pageSize, string? genre, string? search,
        string? mood, string? tempo, bool? instrumental, string? duration)
    {
        return await GetCatalogPagedAsync(page, pageSize, genre, search, null, mood, tempo, instrumental, duration);
    }

    public async Task<TrackResponse?> GetTrackAsync(string trackId)
    {
        if (!Guid.TryParse(trackId, out var id))
            return null;

        var track = await _tracks.GetByIdAsync(id);

        return track is null ? null : await MapToResponseAsync(track);
    }

    /// <summary>Batch-map a page of tracks — 2 queries total instead of 2×N.</summary>
    private async Task<List<TrackResponse>> MapBatchAsync(List<Track> tracks)
    {
        if (tracks.Count == 0)
            return new List<TrackResponse>();

        var creatorIds = tracks
            .Where(t => !string.IsNullOrEmpty(t.CreatorId))
            .Select(t => t.CreatorId)
            .Distinct()
            .ToList();

        // Single query: load all creators for this page
        var creators = await _users.Users
            .Where(u => creatorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u);

        // Single query: load all profile slugs/images for this page
        var profileMap = await _profiles.GetSlugsByUserIdsAsync(creatorIds);

        var result = new List<TrackResponse>(tracks.Count);
        foreach (var t in tracks)
        {
            creators.TryGetValue(t.CreatorId, out var creator);
            profileMap.TryGetValue(t.CreatorId, out var profileInfo);

            var feeRate = creator is not null
                ? TierManifest.For(creator.CreatorTier).FeeRate
                : TierManifest.Free.FeeRate;

            result.Add(BuildTrackResponse(t, feeRate, profileInfo.Slug, profileInfo.ProfileImageUrl));
        }
        return result;
    }

    /// <summary>Map a single track (used by GetTrackAsync — acceptable since it's one item).</summary>
    private async Task<TrackResponse> MapToResponseAsync(Track t)
    {
        decimal feeRate = TierManifest.Free.FeeRate;
        string? creatorSlug = null;
        string? creatorProfileImageUrl = null;
        if (!string.IsNullOrEmpty(t.CreatorId))
        {
            var creator = await _users.FindByIdAsync(t.CreatorId);
            if (creator is not null)
                feeRate = TierManifest.For(creator.CreatorTier).FeeRate;

            var profile = await _profiles.GetByUserIdAsync(t.CreatorId);
            if (profile is not null)
            {
                creatorSlug = profile.Slug;
                creatorProfileImageUrl = profile.ProfileImageUrl;
            }
        }

        return BuildTrackResponse(t, feeRate, creatorSlug, creatorProfileImageUrl);
    }

    private static TrackResponse BuildTrackResponse(Track t, decimal feeRate, string? creatorSlug, string? creatorProfileImageUrl)
    {
        var nonExPrice = t.NonExclusivePriceCents / 100m;
        var exPrice = t.ExclusivePriceCents / 100m;
        var buyoutPrice = t.CopyrightBuyoutPriceCents / 100m;

        return new TrackResponse
        {
            Id = t.Id.ToString(),
            CambrianTrackId = t.CambrianTrackId,
            Title = t.Title,
            Description = t.Description,
            Genre = t.Genre ?? "",
            Mood = t.Mood,
            Tempo = t.Tempo,
            Tags = t.Tags,
            Instrumental = t.Instrumental,
            Visibility = t.Visibility,
            Price = t.Price,
            NonExclusivePrice = nonExPrice,
            ExclusivePrice = exPrice,
            CopyrightBuyoutPrice = buyoutPrice,
            PlatformFeePercent = feeRate,
            NonExclusivePlatformFee = Math.Round(nonExPrice * feeRate, 2),
            NonExclusiveCreatorEarnings = Math.Round(nonExPrice * (1 - feeRate), 2),
            ExclusivePlatformFee = Math.Round(exPrice * feeRate, 2),
            ExclusiveCreatorEarnings = Math.Round(exPrice * (1 - feeRate), 2),
            CopyrightBuyoutPlatformFee = Math.Round(buyoutPrice * feeRate, 2),
            CopyrightBuyoutCreatorEarnings = Math.Round(buyoutPrice * (1 - feeRate), 2),
            ExclusiveSold = t.ExclusiveSold,
            Status = t.Status ?? "available",
            IsCopyrightTransferred = t.CopyrightOwnerId != null,
            LicenseType = t.LicenseType,
            Duration = t.Duration,
            AudioUrl = t.AudioUrl,
            CoverArtUrl = t.CoverArtUrl,
            CreatorId = t.CreatorId,
            CreatorSlug = creatorSlug,
            CreatorProfileImageUrl = creatorProfileImageUrl,
            Artist = !string.IsNullOrWhiteSpace(t.CreatorEntity?.DisplayName)
                ? t.CreatorEntity.DisplayName
                : t.CreatorEntity?.Username
                  ?? t.Creator?.DisplayName
                  ?? "Unknown Artist",
            CreatedAt = t.CreatedAt,
        };
    }
}
