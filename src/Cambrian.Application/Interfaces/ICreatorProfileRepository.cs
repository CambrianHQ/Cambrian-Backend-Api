using Cambrian.Application.DTOs.CreatorProfile;

namespace Cambrian.Application.Interfaces;

public interface ICreatorProfileRepository
{
    Task<CreatorProfileDto?> GetByUserIdAsync(string userId);

    Task<CreatorProfileDto?> GetBySlugAsync(string slug);

    Task<CreatorProfileDto> UpsertAsync(string userId, string slug, string bio, string? niche,
        string? socialLinksJson, bool showEarnings, bool showDownloadStats,
        string? bannerImageUrl = null, string? profileImageUrl = null);

    Task<CreatorProfileDto> UpdateImageAsync(string userId, string? bannerImageUrl, string? profileImageUrl);

    Task<CreatorProfileDto> UpdatePinnedTracksAsync(string userId, string pinnedTrackIds);

    Task<IReadOnlyList<TrackCollectionDto>> GetCollectionsAsync(string creatorId);

    Task<TrackCollectionDto?> GetCollectionByIdAsync(Guid id);

    Task<string?> GetCollectionOwnerAsync(Guid id);

    Task<TrackCollectionDto> AddCollectionAsync(string creatorId, string title, string? description, string trackIds);

    Task<TrackCollectionDto> UpdateCollectionAsync(Guid id, string creatorId, string? title, string? description, string? trackIds);

    Task DeleteCollectionAsync(Guid id);
}
