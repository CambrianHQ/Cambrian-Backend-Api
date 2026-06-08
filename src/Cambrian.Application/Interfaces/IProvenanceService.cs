using Cambrian.Application.DTOs.Provenance;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Manages a track's anchor record under the batched-Merkle model. On upload it records a
/// <c>pending</c> anchor (a worklist entry); the batch-2 job fills in the Merkle root / proof /
/// tx once the batch is anchored on-chain.
/// </summary>
public interface IProvenanceService
{
    /// <summary>Idempotently record a <c>pending</c> anchor for the track. No chain write.</summary>
    Task EnsureAnchorPendingAsync(Guid trackId, string contentHash, CancellationToken ct = default);

    /// <summary>Read the track's anchor state (defaults to <c>pending</c> when no row exists).</summary>
    Task<ProvenanceAnchorState> GetAnchorStateAsync(Guid trackId, CancellationToken ct = default);
}
