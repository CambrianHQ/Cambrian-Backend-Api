using Cambrian.Api.Common;
using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.CreatorProfile;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("creator")]
[Authorize]
[RequireCreatorTier]
[RequireUsername]
[EnableRateLimiting("auth")]
public class CreatorController : BaseController
{
    private readonly ICreatorService _creator;
    private readonly ITrackRepository _tracks;
    private readonly ICreatorIdentityRepository _creators;
    private readonly ICreatorProfileRepository _profiles;
    private readonly IUploadService _upload;
    private readonly ILogger<CreatorController> _logger;

    public CreatorController(
        ICreatorService creator,
        ITrackRepository tracks,
        ICreatorIdentityRepository creators,
        ICreatorProfileRepository profiles,
        IUploadService upload,
        ILogger<CreatorController> logger)
    {
        _creator = creator;
        _tracks = tracks;
        _creators = creators;
        _profiles = profiles;
        _upload = upload;
        _logger = logger;
    }

    [HttpGet("tracks")]
    public async Task<IActionResult> Tracks([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 50;

        var userId = GetRequiredUserId()!;
        var paged = await _creator.GetTracksAsync(userId, page, pageSize);
        foreach (var t in paged.Items)
        {
            t.AudioUrl = ResolveAbsoluteUrl($"/stream/{t.Id}/audio");
            if (!string.IsNullOrEmpty(t.CoverArtUrl))
                t.CoverArtUrl = ResolveImageUrl(t.CoverArtUrl);
        }
        return Ok(new CatalogPageResponse
        {
            Data = paged.Items,
            Page = paged.Page,
            PageSize = paged.PageSize,
            TotalCount = paged.TotalCount,
            TotalPages = paged.TotalPages,
            HasNextPage = paged.HasNextPage,
            HasPreviousPage = paged.HasPreviousPage,
        });
    }

    [HttpGet("revenue")]
    public async Task<IActionResult> Revenue()
    {
        var userId = GetRequiredUserId()!;
        var revenue = await _creator.GetRevenueAsync(userId);
        return OkResponse(revenue);
    }

    [Authorize(Policy = "CanEditOwnTrack")]
    [HttpPut("tracks/{trackId:guid}")]
    public async Task<IActionResult> EditTrack(Guid trackId, [FromBody] EditTrackRequest request)
    {
        var userId = GetRequiredUserId()!;
        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null) return NotFoundResponse("Track not found.");

        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var ownsLegacy = track.CreatorId == userId;
        var ownsUuid = creatorUuid.HasValue && track.CreatorUuid == creatorUuid.Value;
        if (!ownsLegacy && !ownsUuid) return ForbiddenResponse("You can only edit your own tracks.");

        if (request.Title is not null) track.Title = request.Title;
        if (request.Description is not null) track.Description = request.Description;
        if (request.PrimaryGenre is not null || request.Subgenre is not null || request.Genre is not null)
            ApplyGenreUpdates(track, request.PrimaryGenre, request.Subgenre, request.Genre);
        if (request.Mood is not null) track.Mood = request.Mood;
        if (request.Tempo is not null) track.Tempo = request.Tempo;
        if (request.Tags is not null) track.Tags = request.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (request.NonExclusivePriceCents.HasValue)
        {
            track.NonExclusivePriceCents = request.NonExclusivePriceCents.Value;
            track.Price = request.NonExclusivePriceCents.Value / 100m;
        }
        if (request.ExclusivePriceCents.HasValue) track.ExclusivePriceCents = request.ExclusivePriceCents.Value;
        if (request.CopyrightBuyoutPriceCents.HasValue) track.CopyrightBuyoutPriceCents = request.CopyrightBuyoutPriceCents.Value;

        await _tracks.UpdateAsync(track);
        return OkResponse(await BuildMutationResponseAsync(userId, track));
    }

    [Authorize(Policy = "CanEditOwnTrack")]
    [HttpPut("tracks/{trackId:guid}/cover-art")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UpdateTrackCoverArt(Guid trackId, [FromForm] UpdateTrackCoverArtRequest request)
    {
        var userId = GetRequiredUserId()!;
        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null) return NotFoundResponse("Track not found.");

        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var ownsLegacy = track.CreatorId == userId;
        var ownsUuid = creatorUuid.HasValue && track.CreatorUuid == creatorUuid.Value;
        if (!ownsLegacy && !ownsUuid) return ForbiddenResponse("You can only edit your own tracks.");

        if (request.CoverArt is null || request.CoverArt.Length <= 0)
            return ErrorResponse("Cover art file is required.");

        track.CoverArtUrl = await _upload.UploadCoverArtAsync(userId, request.CoverArt);
        await _tracks.UpdateAsync(track);

        return OkResponse(await BuildMutationResponseAsync(userId, track));
    }

    [Authorize(Policy = "CanDeleteOwnTrack")]
    [HttpDelete("tracks/{trackId:guid}")]
    public async Task<IActionResult> DeleteTrack(Guid trackId)
    {
        var userId = GetRequiredUserId()!;
        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null) return NotFoundResponse("Track not found.");

        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var ownsLegacy = track.CreatorId == userId;
        var ownsUuid = creatorUuid.HasValue && track.CreatorUuid == creatorUuid.Value;
        if (!ownsLegacy && !ownsUuid) return ForbiddenResponse("You can only delete your own tracks.");

        try
        {
            await RemoveTrackFromCollectionsAsync(userId, track.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Creator track delete collection cleanup failed. UserId={UserId} TrackId={TrackId}",
                userId,
                track.Id);
        }

        await _tracks.DeleteAsync(track.Id);

        return OkResponse(new
        {
            deleted = true,
            trackId = track.Id,
            cambrianTrackId = track.CambrianTrackId
        }, "Track deleted successfully.");
    }

    private async Task<object> BuildMutationResponseAsync(string userId, Domain.Entities.Track track)
    {
        var linkedCollection = await FindLinkedCollectionAsync(userId, track.Id);
        var pricing = TrackPricingSnapshot.FromTrack(track);

        return new
        {
            id = track.Id,
            cambrianTrackId = track.CambrianTrackId,
            title = track.Title,
            description = track.Description,
            genre = track.Subgenre ?? track.Genre ?? track.PrimaryGenre,
            primaryGenre = track.PrimaryGenre,
            subgenre = track.Subgenre,
            mood = track.Mood,
            tempo = track.Tempo,
            tags = track.Tags,
            coverArtUrl = string.IsNullOrWhiteSpace(track.CoverArtUrl) ? null : ResolveImageUrl(track.CoverArtUrl),
            price = pricing.Price,
            nonExclusivePrice = pricing.NonExclusivePrice,
            exclusivePrice = pricing.ExclusivePrice,
            copyrightBuyoutPrice = pricing.CopyrightBuyoutPrice,
            nonExclusivePriceCents = pricing.NonExclusivePriceCents,
            exclusivePriceCents = pricing.ExclusivePriceCents,
            copyrightBuyoutPriceCents = pricing.CopyrightBuyoutPriceCents,
            collectionId = linkedCollection?.Id,
            collectionTitle = linkedCollection?.Title
        };
    }

    private async Task<TrackCollectionDto?> FindLinkedCollectionAsync(string userId, Guid trackId)
    {
        var trackIdString = trackId.ToString();
        var collections = await _profiles.GetCollectionsAsync(userId);
        return collections.FirstOrDefault(c => c.TrackIds.Contains(trackIdString, StringComparer.OrdinalIgnoreCase));
    }

    private async Task RemoveTrackFromCollectionsAsync(string userId, Guid trackId)
    {
        var trackIdString = trackId.ToString();
        var collections = await _profiles.GetCollectionsAsync(userId);
        foreach (var collection in collections.Where(c => c.TrackIds.Contains(trackIdString, StringComparer.OrdinalIgnoreCase)))
        {
            var updatedTrackIds = string.Join(",",
                collection.TrackIds
                    .Where(id => !string.Equals(id, trackIdString, StringComparison.OrdinalIgnoreCase)));

            await _profiles.UpdateCollectionAsync(
                Guid.Parse(collection.Id),
                userId,
                null,
                null,
                null,
                updatedTrackIds);
        }
    }

    private static void ApplyGenreUpdates(Domain.Entities.Track track, string? primaryGenre, string? subgenre, string? legacyGenre)
    {
        var normalizedPrimary = NormalizeNullableText(primaryGenre);
        var normalizedSubgenre = NormalizeNullableText(subgenre);
        var normalizedLegacy = NormalizeNullableText(legacyGenre);

        if (primaryGenre is not null)
            track.PrimaryGenre = normalizedPrimary;

        if (subgenre is not null)
            track.Subgenre = normalizedSubgenre;
        else if (legacyGenre is not null)
            track.Subgenre = normalizedLegacy;

        track.Genre = track.Subgenre ?? track.PrimaryGenre ?? normalizedLegacy;
    }

private static string? NormalizeNullableText(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
