using Cambrian.Application.DTOs.Admin;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Admin-only operational boundary for inspecting and safely repairing the
/// qualified-play ledger projections.
/// </summary>
public interface IPlayReconciliationService
{
    Task<PlayReconciliationReport> InspectAsync(
        PlayReconciliationRequest request,
        string requestingAdminId,
        CancellationToken ct = default);

    Task<PlayReconciliationRepairResult> RepairAsync(
        PlayReconciliationRepairRequest request,
        string requestingAdminId,
        CancellationToken ct = default);

    /// <summary>Read-only, non-audited summary for the protected health route.</summary>
    Task<PlayPipelineHealthDetails> GetHealthDetailsAsync(CancellationToken ct = default);
}
