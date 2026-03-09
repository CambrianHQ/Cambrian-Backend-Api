using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("admin/data-integrity")]
[Authorize(Roles = "Admin")]
public class DataIntegrityController : BaseController
{
    private readonly IDataIntegrityService _integrity;

    public DataIntegrityController(IDataIntegrityService integrity)
    {
        _integrity = integrity;
    }

    [HttpGet("audit")]
    public async Task<IActionResult> FullAudit()
    {
        var report = await _integrity.RunFullAuditAsync();
        return OkResponse(report);
    }

    [HttpGet("check/purchase-library")]
    public async Task<IActionResult> CheckPurchaseLibrary()
    {
        var violations = await _integrity.CheckPurchaseLibraryConsistencyAsync();
        return OkResponse(new { count = violations.Count, violations });
    }

    [HttpGet("check/exclusive-licensing")]
    public async Task<IActionResult> CheckExclusiveLicensing()
    {
        var violations = await _integrity.CheckExclusiveLicensingIntegrityAsync();
        return OkResponse(new { count = violations.Count, violations });
    }

    [HttpGet("check/payout-integrity")]
    public async Task<IActionResult> CheckPayoutIntegrity()
    {
        var violations = await _integrity.CheckPayoutIntegrityAsync();
        return OkResponse(new { count = violations.Count, violations });
    }

    [HttpGet("check/orphaned-payouts")]
    public async Task<IActionResult> CheckOrphanedPayouts()
    {
        var violations = await _integrity.CheckOrphanedPayoutsAsync();
        return OkResponse(new { count = violations.Count, violations });
    }

    [HttpPost("repair/missing-library-entries")]
    public async Task<IActionResult> RepairMissingLibraryEntries()
    {
        var repaired = await _integrity.RepairMissingLibraryEntriesAsync();
        return OkResponse(new { repairedCount = repaired, message = $"Created {repaired} missing library entries." });
    }

    [HttpPost("repair/exclusive-flags")]
    public async Task<IActionResult> RepairExclusiveFlags()
    {
        var repaired = await _integrity.RepairExclusiveFlagsAsync();
        return OkResponse(new { repairedCount = repaired, message = $"Fixed {repaired} tracks with incorrect ExclusiveSold flags." });
    }
}
