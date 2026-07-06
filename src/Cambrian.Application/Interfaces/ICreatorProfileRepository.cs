using Cambrian.Application.DTOs.CreatorProfile;

namespace Cambrian.Application.Interfaces;

public interface ICreatorProfileRepository
{
    Task<CreatorProfileDto?> GetByUserIdAsync(string userId);

    /// <summary>Batch-load lightweight profile info for multiple creators (slug + profile image only).</summary>
    Task<Dictionary<string, (string? Slug, string? ProfileImageUrl)>> GetSlugsByUserIdsAsync(IEnumerable<string> userIds);

    Task<CreatorProfileDto?> GetBySlugAsync(string slug);

    Task<CreatorProfileDto> UpsertAsync(string userId, string slug, string bio, string? niche,
        string? socialLinksJson, bool showEarnings, bool showDownloadStats,
        string? bannerImageUrl = null, string? profileImageUrl = null,
        string? studioSetupJson = null, string? journeyEntriesJson = null);

    Task<CreatorProfileDto> UpdateImageAsync(string userId, string? bannerImageUrl, string? profileImageUrl);

    /// <summary>
    /// Update canonical presentation fields on an existing CreatorProfile.
    /// Only non-null parameters are applied. Returns null if no profile exists.
    /// </summary>
    Task<CreatorProfileDto?> UpdatePresentationFieldsAsync(string userId, string? bio, string? socialLinksJson, string? bannerImageUrl, string? profileImageUrl);

    Task<CreatorProfileDto> UpdatePinnedTracksAsync(string userId, string pinnedTrackIds);

    Task<CreatorProfileDto?> UpdateSettingsAsync(string userId, bool? showEarnings, bool? showDownloadStats);

    Task<IReadOnlyList<TrackCollectionDto>> GetCollectionsAsync(string creatorId);

    Task<TrackCollectionDto?> GetCollectionByIdAsync(Guid id);

    Task<string?> GetCollectionOwnerAsync(Guid id);

    /// <summary>
    /// Resolve a publicly-visible album (public | unlisted) by its slug. Slugs
    /// are only unique per creator, so this returns the most recent match and
    /// never surfaces draft/private albums. Returns null when no public album
    /// with that slug exists.
    /// </summary>
    Task<TrackCollectionDto?> GetPublicCollectionBySlugAsync(string slug);

    /// <summary>
    /// Creates an album. TrackIds CSV order is the track order; a per-creator
    /// unique slug is generated from the title. AlbumTrack join rows are
    /// dual-written alongside the legacy CSV column.
    /// </summary>
    Task<TrackCollectionDto> AddCollectionAsync(string creatorId, string title, string? description, string? coverImageUrl, string trackIds,
        string? visibility = null, DateTime? releaseDate = null);

    /// <summary>
    /// Partial album update (null = keep). TrackIds CSV order is the new track
    /// order (drag-reorder just sends the reordered CSV). Pass
    /// <paramref name="clearReleaseDate"/> to remove a stored release date.
    /// </summary>
    Task<TrackCollectionDto> UpdateCollectionAsync(Guid id, string creatorId, string? title, string? description, string? coverImageUrl, string? trackIds,
        string? visibility = null, DateTime? releaseDate = null, bool clearReleaseDate = false);

    Task DeleteCollectionAsync(Guid id);
}
