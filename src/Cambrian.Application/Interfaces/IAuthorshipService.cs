using Cambrian.Application.DTOs.Provenance;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Manages a track's authorship document (upsert + read) and the track-level
/// commercial-rights attestation that travels with it.
/// </summary>
public interface IAuthorshipService
{
    /// <summary>Return the authorship document, or null if none has been recorded.</summary>
    Task<TrackAuthorshipResponse?> GetAsync(Track track, CancellationToken ct = default);

    /// <summary>
    /// Idempotently create or replace the track's authorship document and persist the
    /// track's commercial-rights flag from <paramref name="request"/>.
    /// </summary>
    Task<TrackAuthorshipResponse> UpsertAsync(Track track, TrackAuthorshipRequest request, CancellationToken ct = default);
}
