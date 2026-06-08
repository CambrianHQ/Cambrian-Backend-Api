using Cambrian.Application.DTOs.Provenance;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Computes a track's deterministic compliance score from concrete checks
/// (commercial-rights, authorship, AI disclosure, provenance anchoring, metadata).
/// Rules live in one place so checks are easy to add/re-weight.
/// </summary>
public interface IComplianceScoreService
{
    Task<ComplianceScoreResponse> ComputeAsync(Track track, CancellationToken ct = default);
}
