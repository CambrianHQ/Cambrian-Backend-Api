using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("admin")]
[Authorize(Roles = "Admin")]
public class AdminController : BaseController
{
    private readonly IAdminService _admin;
    private readonly IMarketplaceIntegrityService _integrity;

    public AdminController(IAdminService admin, IMarketplaceIntegrityService integrity)
    {
        _admin = admin;
        _integrity = integrity;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard()
    {
        var summary = await _admin.GetDashboardAsync();
        return OkResponse(summary);
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit()
    {
        var logs = await _admin.GetAuditLogsAsync();
        return OkResponse(logs);
    }

    [HttpGet("integrity")]
    public async Task<IActionResult> IntegrityAudit()
    {
        var report = await _integrity.RunAuditAsync();
        return OkResponse(report);
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users()
    {
        var users = await _admin.GetUsersAsync();
        return OkResponse(users);
    }

    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        return OkResponse(new
        {
            platformFeePercent = 20.0,
            paidTierPrice = 9.99,
            creatorTierPrice = 14.99,
            featureToggles = new { payoutsEnabled = true, moderationEnabled = true, marketplaceEnabled = true },
            marketplace = new { allowExclusiveListings = true, requireTrackReview = false }
        });
    }

    [HttpPost("settings")]
    public IActionResult UpdateSettings()
    {
        return OkResponse(new { success = true, message = "Settings updated." });
    }

    // --- Payout management ---

    [HttpGet("payouts/requests")]
    public IActionResult PayoutRequests()
    {
        return OkResponse(Array.Empty<object>());
    }

    [HttpPost("payouts/{id}/approve")]
    public IActionResult ApprovePayout(string id)
    {
        return OkResponse(new { success = true, message = $"Payout {id} approved." });
    }

    [HttpPost("payouts/{id}/reject")]
    public IActionResult RejectPayout(string id)
    {
        return OkResponse(new { success = true, message = $"Payout {id} rejected." });
    }

    // --- User management ---

    public record SetRoleRequest(string Role);
    public record SuspendRequest(string? Reason);

    [HttpPost("users/{id}/role")]
    public async Task<IActionResult> SetUserRole(string id, [FromBody] SetRoleRequest? body)
    {
        var role = body?.Role ?? "User";
        var ok = await _admin.SetUserRoleAsync(id, role);
        if (!ok) return NotFound(new { success = false, message = "User not found." });
        return OkResponse(new { success = true, message = $"Role updated to {role}." });
    }

    [HttpPost("users/{id}/suspend")]
    public async Task<IActionResult> SuspendUser(string id, [FromBody] SuspendRequest? body)
    {
        var ok = await _admin.SuspendUserAsync(id, body?.Reason);
        if (!ok) return NotFound(new { success = false, message = "User not found." });
        return OkResponse(new { success = true, message = "User suspended." });
    }

    [HttpPost("users/{id}/reactivate")]
    public async Task<IActionResult> ReactivateUser(string id)
    {
        var ok = await _admin.ReactivateUserAsync(id);
        if (!ok) return NotFound(new { success = false, message = "User not found." });
        return OkResponse(new { success = true, message = "User reactivated." });
    }

    [HttpPost("users/{id}/reset-password")]
    public IActionResult ResetUserPassword(string id)
    {
        return OkResponse(new { success = true, message = "Password reset." });
    }

    [HttpPost("users/{id}/verify-creator")]
    public async Task<IActionResult> VerifyCreator(string id)
    {
        var ok = await _admin.VerifyCreatorAsync(id);
        if (!ok) return NotFound(new { success = false, message = "User not found." });
        return OkResponse(new { success = true, message = "Creator verified." });
    }

    // --- Content moderation ---

    [HttpGet("reports")]
    public IActionResult Reports()
    {
        return OkResponse(Array.Empty<object>());
    }

    [HttpPost("reports/{id}/investigate")]
    public IActionResult InvestigateReport(string id)
    {
        return OkResponse(new { success = true, message = "Report under investigation." });
    }

    [HttpPost("tracks/{id}/remove")]
    public async Task<IActionResult> RemoveTrack(string id)
    {
        var ok = await _admin.RemoveTrackAsync(id);
        if (!ok) return NotFound(new { success = false, message = "Track not found." });
        return OkResponse(new { success = true, message = "Track removed." });
    }

    [HttpPost("tracks/{id}/restore")]
    public async Task<IActionResult> RestoreTrack(string id)
    {
        var ok = await _admin.RestoreTrackAsync(id);
        if (!ok) return NotFound(new { success = false, message = "Track not found." });
        return OkResponse(new { success = true, message = "Track restored." });
    }

    [HttpPost("tracks/{id}/hide")]
    public async Task<IActionResult> HideTrack(string id)
    {
        var ok = await _admin.HideTrackAsync(id);
        if (!ok) return NotFound(new { success = false, message = "Track not found." });
        return OkResponse(new { success = true, message = "Track hidden." });
    }

    [HttpPost("tracks/{id}/flag")]
    public async Task<IActionResult> FlagTrack(string id)
    {
        var ok = await _admin.FlagTrackAsync(id);
        if (!ok) return NotFound(new { success = false, message = "Track not found." });
        return OkResponse(new { success = true, message = "Track flagged." });
    }

    [HttpPost("tracks/{id}/feature")]
    public IActionResult FeatureTrack(string id)
    {
        return OkResponse(new { success = true, message = "Track featured." });
    }

    [HttpPost("tracks/{id}/pin")]
    public IActionResult PinTrack(string id)
    {
        return OkResponse(new { success = true, message = "Track pinned." });
    }

    public record VisibilityRequest(string Visibility);

    [HttpPost("tracks/{id}/visibility")]
    public async Task<IActionResult> SetTrackVisibility(string id, [FromBody] VisibilityRequest? body)
    {
        var visibility = body?.Visibility ?? "public";
        var ok = await _admin.SetTrackVisibilityAsync(id, visibility);
        if (!ok) return NotFound(new { success = false, message = "Track not found." });
        return OkResponse(new { success = true, message = "Track visibility updated." });
    }

    // --- Collections and tags ---

    [HttpPost("collections/curate")]
    public IActionResult CurateCollection()
    {
        return OkResponse(new { success = true, message = "Collection curated." });
    }

    [HttpPost("tags/manage")]
    public IActionResult ManageTags()
    {
        return OkResponse(new { success = true, message = "Tags updated." });
    }

    /// <summary>
    /// Permanently delete all test/mock data (users, tracks, purchases, etc.).
    /// Preserves only the admin account. Requires ?confirm=yes query parameter.
    /// </summary>
    [HttpPost("purge-test-data")]
    public async Task<IActionResult> PurgeTestData([FromQuery] string confirm)
    {
        if (confirm != "yes")
            return BadRequest(new { error = "Pass ?confirm=yes to confirm destructive operation." });

        var adminEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                      ?? User.FindFirst("email")?.Value
                      ?? "";
        if (string.IsNullOrWhiteSpace(adminEmail))
            return BadRequest(new { error = "Cannot determine admin email from token." });

        var result = await _admin.PurgeTestDataAsync(adminEmail);
        return OkResponse(result);
    }
}
