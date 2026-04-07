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
        string? bannerImageUrl = null, string? profileImageUrl = null);

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

    Task<TrackCollectionDto> AddCollectionAsync(string creatorId, string title, string? description, string trackIds);

    Task<TrackCollectionDto> UpdateCollectionAsync(Guid id, string creatorId, string? title, string? description, string? trackIds);

    Task DeleteCollectionAsync(Guid id);
}
