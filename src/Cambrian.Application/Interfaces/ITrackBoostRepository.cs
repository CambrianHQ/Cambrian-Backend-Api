using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

/// <summary>A track and its boost count within a ranking window.</summary>
public sealed record RankedTrack(Track Track, int BoostCount);

public interface ITrackBoostRepository
{
    Task<TrackBoost?> GetByUserAndTrackAsync(string userId, Guid trackId);

    /// <summary>
    /// Public tracks ranked by number of boosts received since <paramref name="since"/>
    /// (the rolling window), highest first, paginated. A short window keeps the chart
    /// fresh and lets newcomers win — never rank by all-time boosts.
    /// </summary>
    Task<IReadOnlyList<RankedTrack>> GetHotSinceAsync(DateTime since, int skip, int take);

    /// <summary>Total number of distinct public tracks boosted since <paramref name="since"/>.</summary>
    Task<int> CountHotSinceAsync(DateTime since);

    /// <summary>Of <paramref name="trackIds"/>, which the given user has boosted (batch hasBoosted).</summary>
    Task<IReadOnlyCollection<Guid>> GetBoostedTrackIdsAsync(string userId, IReadOnlyCollection<Guid> trackIds);

    /// <summary>
    /// Inserts a boost. Idempotent: a UNIQUE (UserId, TrackId) violation from a
    /// concurrent insert is swallowed so callers can treat boosting as safe to retry.
    /// </summary>
    Task AddAsync(TrackBoost boost);

    Task RemoveAsync(Guid id);

    Task<int> CountByTrackAsync(Guid trackId);
}
