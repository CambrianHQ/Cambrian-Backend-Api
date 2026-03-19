using Cambrian.Domain.Entities;

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

    Task<List<Track>> GetByCreatorIdAsync(string creatorId);

    /// <summary>
    /// Returns public storefront-safe tracks for a creator: not deleted, not hidden,
    /// not draft/private, not copyright-transferred. Ordered by creation date descending.
    /// </summary>
    Task<List<Track>> GetStorefrontTracksAsync(string creatorId);

    /// <summary>Count tracks matching the given filters (for pagination metadata).</summary>
    Task<int> CountAsync(string? genre = null, string? search = null,
        string? mood = null, string? tempo = null, bool? instrumental = null, string? duration = null);

    /// <summary>
    /// Check if a track with the given audio file hash already exists for this creator.
    /// Returns the existing track if found, null otherwise.
    /// </summary>
    Task<Track?> FindByCreatorAndHashAsync(string creatorId, string audioFileHash);

    Task AddAsync(Track track);

    Task UpdateAsync(Track track);

    Task DeleteAsync(Guid id);

    /// <summary>
    /// Atomically set ExclusiveSold = true only if it is currently false.
    /// Returns true if the flag was set, false if the track was already sold exclusively.
    /// Prevents race conditions on concurrent exclusive purchase attempts.
    /// </summary>
    Task<bool> TryMarkExclusiveSoldAsync(Guid trackId);
}
