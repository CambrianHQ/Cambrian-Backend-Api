using Cambrian.Application.DTOs.Readiness;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace Cambrian.Api.Services;

/// <summary>
/// IMemoryCache-backed readiness cache. A short TTL backstops any missed
/// invalidation; explicit <see cref="Invalidate"/> calls keep reads fresh on
/// the known track-write paths.
/// </summary>
public sealed class MemoryTrackReadinessCache : ITrackReadinessCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly IMemoryCache _cache;

    public MemoryTrackReadinessCache(IMemoryCache cache) => _cache = cache;

    public TrackReadinessResponse? Get(Guid trackId) =>
        _cache.TryGetValue(Key(trackId), out TrackReadinessResponse? value) ? value : null;

    public void Set(Guid trackId, TrackReadinessResponse value) =>
        _cache.Set(Key(trackId), value, Ttl);

    public void Invalidate(Guid trackId) => _cache.Remove(Key(trackId));

    private static string Key(Guid trackId) => $"track-readiness:{trackId}";
}
