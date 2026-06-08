using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

/// <summary>Persistence for the per-track <see cref="TrackAuthorship"/> document.</summary>
public interface ITrackAuthorshipRepository
{
    Task<TrackAuthorship?> GetByTrackIdAsync(Guid trackId, CancellationToken ct = default);

    Task<TrackAuthorship> AddAsync(TrackAuthorship row, CancellationToken ct = default);

    Task UpdateAsync(TrackAuthorship row, CancellationToken ct = default);
}
