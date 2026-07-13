using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.CreatorProfile;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace Cambrian.Application.Services;

public sealed class StorefrontService : IStorefrontService
{
    private readonly ICreatorProfileRepository _profiles;
    private readonly ITrackRepository _tracks;
    private readonly IPurchaseRepository _purchases;
    private readonly ICreatorIdentityRepository _creators;
    private readonly IPlayCountService _playCounts;

    public StorefrontService(
        ICreatorProfileRepository profiles,
        ITrackRepository tracks,
        IPurchaseRepository purchases,
        ICreatorIdentityRepository creators,
        IPlayCountService playCounts)
    {
        _profiles = profiles;
        _tracks = tracks;
        _purchases = purchases;
        _creators = creators;
        _playCounts = playCounts;
    }

    public async Task<StorefrontResponse?> GetStorefrontAsync(string slug)
    {
        var profile = await _profiles.GetBySlugAsync(slug);

        // Fall back to Creator.Username (canonical routing identifier)
        if (profile is null)
        {
            var creatorByUsername = await _creators.GetByUsernameAsync(slug);
            if (creatorByUsername is not null)
                profile = await _profiles.GetByUserIdAsync(creatorByUsername.UserId);
        }

        if (profile is null) return null;

        // Resolve creator UUID for dual-FK track lookup
        var creatorUuid = await _creators.GetCreatorIdForUserAsync(profile.UserId);

        // EF Core does not support concurrent operations on a single DbContext,
        // so these must run sequentially — not via Task.WhenAll.
        var tracks = await _tracks.GetStorefrontTracksAsync(profile.UserId, creatorUuid);
        var collections = await _profiles.GetCollectionsAsync(profile.UserId);
        var purchases = await _purchases.GetByCreatorIdAsync(profile.UserId, creatorUuid);

        // Map tracks to responses (pass profile for image fallback when CreatorEntity is null)
        var trackResponses = tracks.Select(t => MapTrack(t, profile)).ToList();

        // Populate per-track plays/sales — without this every track on a storefront showed
        // zero plays even though the same track's own detail page showed a real count.
        var trackIds = trackResponses
            .Select(t => Guid.TryParse(t.Id, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();
        if (trackIds.Count > 0)
        {
            var playCounts = await _playCounts.GetTrackPlayCountsAsync(trackIds);
            var salesCounts = await _purchases.GetCompletedCountsByTrackIdsAsync(trackIds);
            foreach (var track in trackResponses)
            {
                if (!Guid.TryParse(track.Id, out var trackId)) continue;
                track.Plays = (int)playCounts.GetValueOrDefault(trackId);
                track.Sales = salesCounts.GetValueOrDefault(trackId);
            }
        }

        // Build stats from completed purchases. Earnings are intentionally NOT
        // included here — this storefront is served anonymously (F18).
        var completedPurchases = purchases.Where(p => p.Status == "completed").ToList();
        var stats = new CreatorStatsDto
        {
            TotalDownloads = completedPurchases.Count,
            TotalPlays = (int)await _playCounts.GetCreatorTotalPlaysAsync(profile.UserId, creatorUuid),
        };

        // Resolve pinned tracks: creator-managed order, falling back to most recent
        var pinnedTracks = ResolvePinnedTracks(profile.PinnedTrackIds, trackResponses);

        return new StorefrontResponse
        {
            Profile = profile,
            Stats = stats,
            PinnedTracks = pinnedTracks,
            Collections = collections,
            Tracks = trackResponses
        };
    }

    private static IReadOnlyList<TrackResponse> ResolvePinnedTracks(
        string? pinnedTrackIds,
        List<TrackResponse> allTracks)
    {
        if (!string.IsNullOrWhiteSpace(pinnedTrackIds))
        {
            var ids = pinnedTrackIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            var trackLookup = allTracks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
            var pinned = new List<TrackResponse>();
            foreach (var id in ids)
            {
                if (trackLookup.TryGetValue(id, out var track))
                    pinned.Add(track);
            }

            if (pinned.Count > 0)
                return pinned;
        }

        // Fallback: top 5 most recent tracks
        return allTracks.Take(5).ToList();
    }

    private static TrackResponse MapTrack(Track t, CreatorProfileDto? profile = null)
    {
        // Fallback: if NonExclusivePriceCents is 0, use legacy Price field (matches checkout logic)
        var legacyPriceDollars = t.Price;
        var nonExPrice = t.NonExclusivePriceCents > 0 ? t.NonExclusivePriceCents / 100m : legacyPriceDollars;

        return new TrackResponse
        {
            Id = t.Id.ToString(),
            CambrianTrackId = t.CambrianTrackId,
            Title = t.Title,
            Description = t.Description,
            Genre = t.Subgenre ?? t.Genre ?? t.PrimaryGenre ?? "",
            PrimaryGenre = t.PrimaryGenre,
            Subgenre = t.Subgenre,
            Price = nonExPrice,
            NonExclusivePrice = nonExPrice,
            Status = t.Status == "exclusive_sold" || t.Status == "copyright_transferred" ? "available" : (t.Status ?? "available"),
            Duration = t.Duration,
            AudioUrl = t.AudioUrl,
            CoverArtUrl = t.CoverArtUrl,
            CreatorId = t.CreatorId,
            CreatorSlug = t.CreatorEntity?.Username ?? profile?.Username ?? profile?.Slug,
            CreatorProfileImageUrl = profile?.ProfileImageUrl,
            Artist = !string.IsNullOrWhiteSpace(t.CreatorEntity?.DisplayName)
                ? t.CreatorEntity.DisplayName
                : t.CreatorEntity?.Username
                  ?? t.Creator?.DisplayName
                  ?? "Unknown",
            CreatedAt = t.CreatedAt,
        };
    }
}
