using Cambrian.Application.DTOs.Provenance;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Controller-facing coordinator for a track's §9 legitimacy surface. Resolves the
/// track (GUID or <c>CAMB-TRK-*</c>), enforces ownership where required, and delegates
/// to the provenance / authorship / compliance services. Throws
/// <see cref="System.Collections.Generic.KeyNotFoundException"/> (→404) and
/// <see cref="Exceptions.ForbiddenException"/> (→403); plan gating stays at the controller.
/// </summary>
public interface ITrackLegitimacyService
{
    Task<ProvenanceResponse> GetProvenanceAsync(string trackId, CancellationToken ct = default);

    Task<ComplianceScoreResponse> GetComplianceScoreAsync(string trackId, CancellationToken ct = default);

    Task<TrackAuthorshipResponse> GetAuthorshipAsync(string trackId, string userId, bool isAdmin, CancellationToken ct = default);

    Task<TrackAuthorshipResponse> UpsertAuthorshipAsync(string trackId, string userId, bool isAdmin, TrackAuthorshipRequest request, CancellationToken ct = default);

    Task<CreatorTrackDetailResponse> GetCreatorDetailAsync(string trackId, string userId, bool isAdmin, CancellationToken ct = default);
}
