using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.CreatorProfile;
using Cambrian.Application.DTOs.V1;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Albums;

namespace Cambrian.Api.Controllers.v1;

/// <summary>
/// Shared presentation logic for the versioned album controllers. Lives on top
/// of <see cref="BaseController"/> so both the authenticated management surface
/// and the public read surface build identical album/track projections and
/// resolve image/stream URLs the same way.
/// </summary>
public abstract class AlbumV1ControllerBase : BaseController
{
    protected readonly ICreatorProfileRepository Profiles;
    protected readonly ICreatorIdentityRepository Creators;
    protected readonly ICatalogService Catalog;
    protected readonly ITrackVisibilityPolicy TrackVisibility;

    protected AlbumV1ControllerBase(
        ICreatorProfileRepository profiles,
        ICreatorIdentityRepository creators,
        ICatalogService catalog,
        ITrackVisibilityPolicy trackVisibility)
    {
        Profiles = profiles;
        Creators = creators;
        Catalog = catalog;
        TrackVisibility = trackVisibility;
    }

    /// <summary>Project a stored collection into the album summary DTO.</summary>
    protected AlbumV1Dto ToAlbumDto(TrackCollectionDto c, string creatorId)
    {
        var dto = new AlbumV1Dto();
        FillAlbumFields(dto, c, creatorId);
        return dto;
    }

    private void FillAlbumFields(AlbumV1Dto dto, TrackCollectionDto c, string creatorId)
    {
        dto.Id = c.Id;
        dto.CreatorId = creatorId;
        dto.Title = c.Title;
        dto.Slug = c.Slug;
        dto.Description = c.Description;
        dto.ArtworkUrl = string.IsNullOrWhiteSpace(c.CoverImageUrl) ? null : ResolveImageUrl(c.CoverImageUrl);
        dto.Visibility = c.Visibility;
        dto.ReleaseDate = c.ReleaseDate;
        dto.CreatedAt = c.CreatedAt;
        dto.UpdatedAt = c.UpdatedAt;
        dto.TrackIds = c.TrackIds;
        dto.TrackCount = c.TrackIds.Count;
    }

    /// <summary>
    /// Build the full album detail: metadata + creator summary + hydrated tracks
    /// in album order. Tracks the requester can't see (drafts/private) are
    /// filtered out via the shared <see cref="ITrackVisibilityPolicy"/>, and the
    /// hydrated payload uses the anonymous-safe <see cref="PublicCatalogTrackDto"/>.
    /// </summary>
    protected async Task<AlbumDetailV1Dto> BuildAlbumDetailAsync(
        TrackCollectionDto col, string ownerUserId, string? requesterId, bool isAdmin)
    {
        var tracks = new List<PublicCatalogTrackDto>();
        // Sequential on purpose — a single scoped data context, no concurrent queries.
        foreach (var trackId in col.TrackIds)
        {
            var track = await Catalog.GetTrackAsync(trackId);
            if (track is null) continue;
            if (!TrackVisibility.CanAccess(track.Visibility ?? "public", track.CreatorId, requesterId, isAdmin))
                continue;
            track.AudioUrl = ResolveAbsoluteUrl($"/stream/{track.Id}/audio");
            if (!string.IsNullOrEmpty(track.CoverArtUrl))
                track.CoverArtUrl = ResolveImageUrl(track.CoverArtUrl);
            tracks.Add(PublicCatalogTrackDto.From(track));
        }

        var detail = new AlbumDetailV1Dto
        {
            Creator = await BuildCreatorSummaryAsync(ownerUserId),
            Tracks = tracks,
        };
        FillAlbumFields(detail, col, ownerUserId);
        return detail;
    }

    private async Task<AlbumCreatorSummaryV1Dto> BuildCreatorSummaryAsync(string ownerUserId)
    {
        var summary = new AlbumCreatorSummaryV1Dto { UserId = ownerUserId };
        if (string.IsNullOrEmpty(ownerUserId)) return summary;

        var profile = await Profiles.GetByUserIdAsync(ownerUserId);
        var identity = await Creators.ResolveByLegacyIdentifierAsync(ownerUserId);
        summary.CreatorId = identity?.Id;
        summary.Username = identity?.Username;
        summary.Slug = profile?.Slug ?? identity?.Username;
        summary.DisplayName = profile?.DisplayName ?? identity?.DisplayName ?? identity?.Username;
        var image = profile?.ProfileImageUrl ?? identity?.ProfileImageUrl;
        summary.ProfileImageUrl = string.IsNullOrWhiteSpace(image) ? null : ResolveImageUrl(image);
        return summary;
    }

    /// <summary>
    /// Ordered, de-duplicated CSV of track ids. Non-GUID entries are dropped;
    /// callers validate ownership separately so an invalid id can't sneak in.
    /// </summary>
    protected static string BuildOrderedTrackCsv(IEnumerable<string> ids)
    {
        var seen = new HashSet<Guid>();
        var ordered = new List<string>();
        foreach (var raw in ids)
        {
            if (Guid.TryParse(raw?.Trim(), out var g) && seen.Add(g))
                ordered.Add(g.ToString());
        }
        return string.Join(",", ordered);
    }

    /// <summary>Null input keeps the stored value; recognized values normalize; anything else is invalid.</summary>
    protected static bool TryParseVisibility(string? raw, out string? visibility)
    {
        visibility = null;
        if (raw is null) return true;
        var normalized = AlbumVisibility.Normalize(raw);
        if (normalized is null) return false;
        visibility = normalized;
        return true;
    }
}
