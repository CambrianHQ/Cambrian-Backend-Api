namespace Cambrian.Application.Interfaces;

/// <summary>
/// The single definition of "how many times has this been played" used by every endpoint that
/// reports a play count. Backed by the TrackStats/CreatorStats projection — kept current by the
/// transactional write path in StreamRepository and self-healed by
/// IPlayCountReconciliationService — rather than each caller running its own live
/// COUNT/GROUP BY over StreamSessions. A track or creator with no stats row yet (never played)
/// reports zero, not an error.
/// </summary>
public interface IPlayCountService
{
    Task<long> GetTrackPlayCountAsync(Guid trackId, CancellationToken ct = default);

    /// <summary>Batched lookup for catalog/list pages. Every requested id is present in the result, defaulting to 0.</summary>
    Task<IReadOnlyDictionary<Guid, long>> GetTrackPlayCountsAsync(IReadOnlyCollection<Guid> trackIds, CancellationToken ct = default);

    Task<long> GetTrackUniqueListenerCountAsync(Guid trackId, CancellationToken ct = default);

    /// <summary>
    /// Lifetime plays across every track owned by a creator. Accepts the dual identity used
    /// everywhere else in this codebase: the canonical Creator UUID when known (fast path, reads
    /// CreatorStats), and the legacy ApplicationUser id as a fallback for creators who don't have
    /// a Creator row yet (live-computed, since there is no projection row to key on).
    /// </summary>
    Task<long> GetCreatorTotalPlaysAsync(string legacyUserId, Guid? creatorUuid, CancellationToken ct = default);

    Task<long> GetPlatformTotalPlaysAsync(CancellationToken ct = default);
}
