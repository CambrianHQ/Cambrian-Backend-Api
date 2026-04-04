using Cambrian.Application.AI.Discovery.Builders;
using Cambrian.Application.AI.Discovery.Dtos;
using Cambrian.Application.AI.Discovery.Queries;
using Cambrian.Application.AI.Discovery.Ranking;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.AI.Discovery.Services;

public class TrackDiscoveryService : ITrackDiscoveryService
{
    private readonly ITrackRepository _tracks;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ICreatorIdentityRepository _creators;
    private readonly ICreatorProfileRepository _profiles;
    private readonly ITrackRankingService _ranking;
    private readonly ILogger<TrackDiscoveryService> _logger;

    public TrackDiscoveryService(
        ITrackRepository tracks,
        UserManager<ApplicationUser> users,
        ICreatorIdentityRepository creators,
        ICreatorProfileRepository profiles,
        ITrackRankingService ranking,
        ILogger<TrackDiscoveryService> logger)
    {
        _tracks = tracks;
        _users = users;
        _creators = creators;
        _profiles = profiles;
        _ranking = ranking;
        _logger = logger;
    }

    public async Task<PagedResult<TrackSearchResultDto>> SearchAsync(SearchTracksQuery query)
    {
        // Use existing track repository for structured filtering
        var tracks = await _tracks.BrowseAsync(
            query.Page, query.PageSize,
            query.Genre, query.Query, "trending",
            query.Mood, query.Tempo, query.Instrumental, query.Duration);

        var totalCount = await _tracks.CountAsync(
            query.Genre, query.Query,
            query.Mood, query.Tempo, query.Instrumental, query.Duration);

        if (tracks.Count == 0)
            return new PagedResult<TrackSearchResultDto>
            {
                Items = Array.Empty<TrackSearchResultDto>(),
                Page = query.Page,
                PageSize = query.PageSize,
                TotalCount = totalCount
            };

        // Batch-load creator data (N+1 prevention)
        var creatorIds = tracks.Select(t => t.CreatorId).Distinct().ToList();
        var creatorMap = _users.Users
            .Where(u => creatorIds.Contains(u.Id))
            .ToDictionary(u => u.Id);
        var profileMap = await _profiles.GetSlugsByUserIdsAsync(creatorIds);

        // Score, rank, and build AI responses
        var results = new List<TrackSearchResultDto>(tracks.Count);
        foreach (var track in tracks)
        {
            creatorMap.TryGetValue(track.CreatorId, out var creator);
            profileMap.TryGetValue(track.CreatorId, out var profileInfo);

            var feeRate = creator is not null
                ? TierManifest.For(creator.CreatorTier).FeeRate
                : TierManifest.Free.FeeRate;

            // Apply price filter
            if (query.MaxPriceCents.HasValue)
            {
                var cheapest = GetCheapestPriceCents(track);
                if (cheapest > query.MaxPriceCents.Value) continue;
            }

            // Apply license type filter
            if (!string.IsNullOrEmpty(query.LicenseType) && !IsLicenseAvailable(track, query.LicenseType))
                continue;

            var score = _ranking.ComputeScore(track, query);
            var result = TrackAiResponseBuilder.Build(track, score, query, feeRate,
                creator, profileInfo.Slug, profileInfo.ProfileImageUrl);
            results.Add(result);
        }

        // Re-sort by score descending
        results.Sort((a, b) => b.Score.CompareTo(a.Score));

        return new PagedResult<TrackSearchResultDto>
        {
            Items = results,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = totalCount
        };
    }

    public async Task<TrackDetailsDto?> GetDetailsAsync(GetTrackDetailsQuery query)
    {
        var track = await ResolveTrackAsync(query.TrackId);
        if (track is null) return null;

        var creator = await _users.FindByIdAsync(track.CreatorId);
        var profileMap = await _profiles.GetSlugsByUserIdsAsync(new[] { track.CreatorId });
        profileMap.TryGetValue(track.CreatorId, out var profileInfo);

        var feeRate = creator is not null
            ? TierManifest.For(creator.CreatorTier).FeeRate
            : TierManifest.Free.FeeRate;

        return TrackAiResponseBuilder.BuildDetails(track, feeRate,
            creator, profileInfo.Slug, profileInfo.ProfileImageUrl);
    }

    public async Task<List<LicenseOptionDto>> GetLicenseOptionsAsync(GetLicenseOptionsQuery query)
    {
        var track = await ResolveTrackAsync(query.TrackId);
        if (track is null) return new List<LicenseOptionDto>();

        return TrackAiResponseBuilder.BuildLicenseOptions(track);
    }

    public async Task<CreatorProfileDetailDto?> GetCreatorProfileAsync(GetCreatorProfileQuery query)
    {
        // Try by username first, then by userId
        var dto = await _creators.GetByUsernameAsync(query.Identifier);
        dto ??= await _creators.ResolveByLegacyIdentifierAsync(query.Identifier);
        if (dto is null) return null;

        var creatorId = await _creators.GetCreatorIdForUserAsync(dto.UserId);
        var followerCount = creatorId.HasValue
            ? await _creators.GetFollowerCountAsync(creatorId.Value)
            : 0;

        var profile = await _profiles.GetByUserIdAsync(dto.UserId);

        return new CreatorProfileDetailDto
        {
            UserId = dto.UserId,
            Username = dto.Username,
            DisplayName = dto.DisplayName,
            Bio = profile?.Bio ?? dto.Bio,
            Niche = profile?.Niche,
            ProfileImageUrl = dto.ProfileImageUrl,
            CoverImageUrl = dto.CoverImageUrl,
            Slug = dto.Username ?? profile?.Slug,
            TrackCount = dto.Stats.TrackCount,
            FollowerCount = followerCount
        };
    }

    private async Task<Track?> ResolveTrackAsync(string trackId)
    {
        // Try CambrianTrackId first, then Guid
        var track = await _tracks.GetByCambrianTrackIdAsync(trackId);
        if (track is null && Guid.TryParse(trackId, out var guid))
            track = await _tracks.GetByIdAsync(guid);
        return track;
    }

    private static int GetCheapestPriceCents(Track track)
    {
        if (track.NonExclusivePriceCents > 0) return track.NonExclusivePriceCents;
        if (track.Price > 0) return (int)(track.Price * 100);
        return int.MaxValue;
    }

    private static bool IsLicenseAvailable(Track track, string licenseType) => licenseType switch
    {
        "nonexclusive" or "non-exclusive" => true,
        "exclusive" => !track.ExclusiveSold,
        "copyright_buyout" => !track.ExclusiveSold && track.CopyrightOwnerId == null,
        _ => true
    };
}
