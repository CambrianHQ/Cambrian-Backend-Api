using Cambrian.Application.DTOs.Readiness;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Weighted release-readiness scoring (loudness 25, metadata 25, AI disclosure 25,
/// cover 15, provenance 10). Results are cached per track; any track-affecting
/// write must call <see cref="ITrackReadinessCache.Invalidate"/>.
/// </summary>
public interface ITrackReadinessService
{
    /// <summary>Compute (or return cached) readiness for the track. Null when the track is absent.</summary>
    Task<TrackReadinessResponse?> GetAsync(Guid trackId, CancellationToken ct = default);
}

/// <summary>
/// Per-track cache for readiness results. Implemented over IMemoryCache in the API
/// layer; the interface lives here so Application services can invalidate without
/// referencing caching packages.
/// </summary>
public interface ITrackReadinessCache
{
    TrackReadinessResponse? Get(Guid trackId);
    void Set(Guid trackId, TrackReadinessResponse value);
    void Invalidate(Guid trackId);
}
