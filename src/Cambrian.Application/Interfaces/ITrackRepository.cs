using Cambrian.Domain.Entities;
using Cambrian.Application.DTOs.Creator;
using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.Interfaces;

public interface ITrackRepository
{
    Task<List<Track>> BrowseAsync();

    Task<List<Track>> BrowseAsync(int page, int pageSize, string? genre, string? search, string? sort = null);

    /// <summary>Extended browse with mood/tempo/instrumental/duration filters.</summary>
    Task<List<Track>> BrowseAsync(int page, int pageSize, string? genre, string? search, string? sort,
        string? mood, string? tempo, bool? instrumental, string? duration);

    Task<Track?> GetByIdAsync(Guid id);

    /// <summary>
    /// Same as <see cref="GetByIdAsync"/> but does NOT filter out removed (trashed)
    /// tracks — for owner/admin-only actions that need to operate on a track that's
    /// currently in Trash (restore, permanent-delete, viewing trash detail).
    /// </summary>
    Task<Track?> GetByIdIncludingRemovedAsync(Guid id);

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

    /// <summary>
    /// Aggregate live engagement counts (plays, completed sales) for a set of tracks
    /// in two grouped queries — no per-track round-trips. Track IDs with no activity
    /// are returned with zeroed counts so callers can index every id safely.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, TrackStats>> GetTrackStatsAsync(IReadOnlyCollection<Guid> trackIds);

    Task AddAsync(Track track);

    Task UpdateAsync(Track track);

    /// <summary>
    /// Find an active (not removed) track owned by <paramref name="creatorId"/> whose
    /// stored audio has this exact content hash — used to warn a creator they're
    /// about to upload audio they already have a live track for, before a second
    /// copy is created. Deliberately scoped to active tracks: a creator who removed
    /// an old master and re-uploads it (or reuses it in a new album/remaster) is not
    /// a duplicate in the sense that matters here.
    /// </summary>
    Task<Track?> FindActiveByCreatorAndContentHashAsync(string? creatorId, Guid? creatorUuid, string contentHash);

    /// <summary>Soft-delete: moves the track to the creator's Trash. See DeleteAsync's implementation for exactly what is preserved.</summary>
    Task DeleteAsync(Guid id, string? deletedByUserId = null);

    /// <summary>
    /// Owner-facing undo of <see cref="DeleteAsync"/>: restores visibility/status to
    /// what they were immediately before the delete and clears the trash markers.
    /// No-op-safe to call on a track that isn't trashed; throws if the track has
    /// already been permanently purged (see <see cref="RequestPurgeAsync"/>).
    /// </summary>
    Task RestoreAsync(Guid id);

    /// <summary>
    /// Marks a trashed track for permanent purge. Only flips a timestamp — the
    /// actual object-storage deletion happens asynchronously in the purge worker,
    /// so a request-time failure here can never leave storage and the database
    /// inconsistent with each other.
    /// </summary>
    Task RequestPurgeAsync(Guid id);

    /// <summary>Trashed (soft-deleted, not yet purged) tracks owned by this creator, for the Studio Trash view.</summary>
    Task<List<CreatorTrackSummary>> GetTrashedTrackSummariesAsync(string creatorId, Guid? creatorUuid = null);

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
