using Cambrian.Application.DTOs.Admin;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Protected play-ledger maintenance operations. The existing
/// POST /admin/charts/aggregate endpoint is the corresponding bounded chart
/// rebuild trigger.
/// </summary>
[Route("admin/play-reconciliation")]
[Authorize(Roles = "Admin")]
public sealed class PlayReconciliationController : BaseController
{
    private readonly IPlayReconciliationService _reconciliation;

    public PlayReconciliationController(IPlayReconciliationService reconciliation)
    {
        _reconciliation = reconciliation;
    }

    /// <summary>Compare the qualified-play ledger with lifetime projections without repairing them.</summary>
    [HttpPost("dry-run")]
    public async Task<IActionResult> DryRun(
        [FromBody] PlayReconciliationRequest request,
        CancellationToken ct)
    {
        var adminId = GetRequiredUserId();
        if (string.IsNullOrWhiteSpace(adminId))
            return Unauthorized(new { success = false, error = "Authenticated admin identity is missing." });

        var report = await _reconciliation.InspectAsync(request, adminId, ct);
        return OkResponse(report);
    }

    /// <summary>
    /// Repair a bounded set of mismatched TrackStats rows from existing ledger
    /// events. This never creates qualified-play events.
    /// </summary>
    [HttpPost("repair")]
    public async Task<IActionResult> Repair(
        [FromBody] PlayReconciliationRepairRequest request,
        CancellationToken ct)
    {
        var adminId = GetRequiredUserId();
        if (string.IsNullOrWhiteSpace(adminId))
            return Unauthorized(new { success = false, error = "Authenticated admin identity is missing." });

        var result = await _reconciliation.RepairAsync(request, adminId, ct);
        if (!result.LockAcquired)
        {
            return StatusCode(StatusCodes.Status409Conflict, new
            {
                success = false,
                error = "Another play reconciliation repair is already running.",
                data = result,
            });
        }

        return OkResponse(result);
    }
}
