using Cambrian.Domain.Entities;
using Cambrian.Application.DTOs.Creator;

namespace Cambrian.Application.Interfaces;

public interface ITrackRepository
{
    Task<List<Track>> BrowseAsync();

    Task<List<Track>> BrowseAsync(int page, int pageSize, string? genre, string? search, string? sort = null);

    /// <summary>Extended browse with mood/tempo/instrumental/duration filters.</summary>
    Task<List<Track>> BrowseAsync(int page, int pageSize, string? genre, string? search, string? sort,
        string? mood, string? tempo, bool? instrumental, string? duration);

    Task<Track?> GetByIdAsync(Guid id);

    /// <summary>Lookup a track by its Cambrian track ID (CAMB-TRK-XXXX).</summary>
    Task<Track?> GetByCambrianTrackIdAsync(string cambrianTrackId);

    Task<List<Track>> GetByCreatorIdAsync(string creatorId, Guid? creatorUuid = null);

    Task<List<CreatorDashboardTrackSummary>> GetDashboardTrackSummariesAsync(string creatorId, Guid? creatorUuid = null);

    Task<List<CreatorTrackSummary>> GetCreatorTrackSummariesAsync(string creatorId, Guid? creatorUuid = null);

    /// <summary>
    /// Returns public storefront-safe tracks for a creator: not deleted, not hidden,
    /// not draft/private, not copyright-transferred. Ordered by creation date descending.
    /// </summary>
    Task<List<Track>> GetStorefrontTracksAsync(string creatorId, Guid? creatorUuid = null);

    /// <summary>Count tracks matching the given filters (for pagination metadata).</summary>
    Task<int> CountAsync(string? genre = null, string? search = null,
        string? mood = null, string? tempo = null, bool? instrumental = null, string? duration = null);

    Task AddAsync(Track track);

    Task UpdateAsync(Track track);

    Task DeleteAsync(Guid id);

    /// <summary>
    /// Atomically set ExclusiveSold = true only if it is currently false.
    /// Returns true if the flag was set, false if the track was already sold exclusively.
    /// Prevents race conditions on concurrent exclusive purchase attempts.
    /// </summary>
    Task<bool> TryMarkExclusiveSoldAsync(Guid trackId);

    /// <summary>
    /// Atomically mark a track as copyright-transferred. Sets ExclusiveSold, Status,
    /// Visibility, OriginalCreatorId, CopyrightOwnerId, and CopyrightTransferredAt
    /// in a single conditional UPDATE. Returns true if the update succeeded.
    /// Prevents race conditions on concurrent copyright buyout attempts.
    /// </summary>
    Task<bool> TryMarkCopyrightBuyoutAsync(Guid trackId, string buyerUserId);
}
