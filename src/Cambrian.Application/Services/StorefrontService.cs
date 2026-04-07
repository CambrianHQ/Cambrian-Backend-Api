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
    private readonly UserManager<ApplicationUser> _users;
    private readonly ICreatorIdentityRepository _creators;

    public StorefrontService(
        ICreatorProfileRepository profiles,
        ITrackRepository tracks,
        IPurchaseRepository purchases,
        UserManager<ApplicationUser> users,
        ICreatorIdentityRepository creators)
    {
        _profiles = profiles;
        _tracks = tracks;
        _purchases = purchases;
        _users = users;
        _creators = creators;
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

        // Fetch storefront-safe tracks, collections, and stats in parallel
        var tracksTask = _tracks.GetStorefrontTracksAsync(profile.UserId, creatorUuid);
        var collectionsTask = _profiles.GetCollectionsAsync(profile.UserId);
        var purchasesTask = _purchases.GetByCreatorIdAsync(profile.UserId, creatorUuid);

        await Task.WhenAll(tracksTask, collectionsTask, purchasesTask);

        var tracks = tracksTask.Result;
        var collections = collectionsTask.Result;
        var purchases = purchasesTask.Result;

        // Resolve creator fee rate
        var creator = await _users.FindByIdAsync(profile.UserId);
        var feeRate = creator is not null
            ? TierManifest.For(creator.CreatorTier).FeeRate
            : TierManifest.Free.FeeRate;

        // Map tracks to responses (pass profile for image fallback when CreatorEntity is null)
        var trackResponses = tracks.Select(t => MapTrack(t, feeRate, profile)).ToList();

        // Build stats from completed purchases — use per-purchase floor to match wallet credits
        var completedPurchases = purchases.Where(p => p.Status == "completed").ToList();
        var totalEarnedCents = completedPurchases.Sum(p => (long)Math.Floor(p.AmountCents * (1 - feeRate)));
        var stats = new CreatorStatsDto
        {
            TotalDownloads = completedPurchases.Count,
            TotalEarnings = profile.ShowEarnings
                ? totalEarnedCents / 100m
                : 0m
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

    private static TrackResponse MapTrack(Track t, decimal feeRate, CreatorProfileDto? profile = null)
    {
        // Fallback: if *PriceCents fields are 0, use legacy Price field (matches checkout logic)
        var legacyPriceDollars = t.Price;
        var nonExPrice = t.NonExclusivePriceCents > 0 ? t.NonExclusivePriceCents / 100m : legacyPriceDollars;
        var exPrice = t.ExclusivePriceCents > 0 ? t.ExclusivePriceCents / 100m : legacyPriceDollars;
        var buyoutPrice = t.CopyrightBuyoutPriceCents > 0
            ? t.CopyrightBuyoutPriceCents / 100m
            : (t.ExclusivePriceCents > 0 ? t.ExclusivePriceCents / 100m : legacyPriceDollars);

        return new TrackResponse
        {
            Id = t.Id.ToString(),
            CambrianTrackId = t.CambrianTrackId,
            Title = t.Title,
            Description = t.Description,
            Genre = t.Genre ?? "",
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
