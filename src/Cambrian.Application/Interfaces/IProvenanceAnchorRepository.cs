using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

/// <summary>Persistence for the per-track <see cref="ProvenanceAnchor"/> row.</summary>
public interface IProvenanceAnchorRepository
{
    Task<ProvenanceAnchor?> GetByTrackIdAsync(Guid trackId, CancellationToken ct = default);

    Task<ProvenanceAnchor> AddAsync(ProvenanceAnchor row, CancellationToken ct = default);

    Task UpdateAsync(ProvenanceAnchor row, CancellationToken ct = default);

    /// <summary>The anchor batch job's worklist: oldest <c>pending</c> rows, capped at <paramref name="max"/>.</summary>
    Task<List<ProvenanceAnchor>> GetPendingAsync(int max, CancellationToken ct = default);

    /// <summary>Persist a whole anchored batch in one save.</summary>
    Task UpdateRangeAsync(IEnumerable<ProvenanceAnchor> rows, CancellationToken ct = default);
}
