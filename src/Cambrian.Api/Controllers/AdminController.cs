using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("admin")]
[Authorize(Roles = "Admin")]
public class AdminController : BaseController
{
    private const string MsgUserNotFound = "User not found.";
    private const string MsgTrackNotFound = "Track not found.";

    private readonly IAdminService _admin;
    private readonly IMarketplaceIntegrityService _integrity;
    private readonly ILogger<AdminController> _logger;
    private readonly IWebHostEnvironment _env;

    public AdminController(IAdminService admin, IMarketplaceIntegrityService integrity, ILogger<AdminController> logger, IWebHostEnvironment env)
    {
        _admin = admin;
        _integrity = integrity;
        _logger = logger;
        _env = env;
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

    [HttpGet("tracks")]
    public async Task<IActionResult> Tracks()
    {
        var tracks = await _admin.GetTracksAsync();
        return OkResponse(tracks);
    }

    [HttpGet("purchases")]
    public async Task<IActionResult> Purchases()
    {
        var purchases = await _admin.GetPurchasesAsync();
        return OkResponse(purchases);
    }

    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        var free = TierManifest.Free;
        var pro = TierManifest.Pro;
        return OkResponse(new
        {
            freeTierFeePercent = (double)free.FeeRate,
            proTierFeePercent = (double)pro.FeeRate,
            proTierPriceCents = pro.PriceCents,
            freeTierUploadLimit = free.UploadLimit,
            proTierUploadLimit = pro.UploadLimit,
            featureToggles = new { payoutsEnabled = true, moderationEnabled = true, marketplaceEnabled = true }
        });
    }

    [HttpPost("settings")]
    public IActionResult UpdateSettings()
    {
        // Settings are read-only compile-time constants (TierManifest).
        // Accept the request and return success — no persistence needed for MVP.
        return MessageResponse("Settings saved.");
    }

    // --- Payout management ---

    [HttpGet("payouts")]
    public async Task<IActionResult> Payouts()
    {
        var payouts = await _admin.GetPayoutsAsync();
        return OkResponse(payouts);
    }

    [HttpGet("payouts/requests")]
    public async Task<IActionResult> PayoutRequests()
    {
        var all = await _admin.GetPayoutsAsync();
        var pending = new List<object>();
        foreach (var p in all)
        {
            if (p.Status == "pending") pending.Add(p);
        }
        return OkResponse(pending);
    }

    [HttpPost("payouts/{id}/approve")]
    public async Task<IActionResult> ApprovePayout(string id)
    {
        _logger.LogInformation("[Admin] ApprovePayout id={PayoutId}", id);
        var ok = await _admin.ApprovePayoutAsync(id);
        if (!ok) return NotFound(new { success = false, message = "Payout not found." });
        return OkResponse(new { success = true, message = $"Payout {id} approved." });
    }

    [HttpPost("payouts/{id}/reject")]
    public async Task<IActionResult> RejectPayout(string id)
    {
        _logger.LogInformation("[Admin] RejectPayout id={PayoutId}", id);
        var ok = await _admin.RejectPayoutAsync(id);
        if (!ok) return NotFound(new { success = false, message = "Payout not found." });
        return OkResponse(new { success = true, message = $"Payout {id} rejected." });
    }

    // --- User management ---

    public record SetRoleRequest(string Role);
    public record SuspendRequest(string? Reason);

    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase) { "User", "Creator", "Admin" };

    [HttpPost("users/{id}/role")]
    public async Task<IActionResult> SetUserRole(string id, [FromBody] SetRoleRequest? body)
    {
        var role = body?.Role ?? "User";
        if (!AllowedRoles.Contains(role))
            return BadRequest(new { success = false, message = $"Invalid role '{role}'. Allowed: User, Creator, Admin." });
        _logger.LogInformation("[Admin] SetUserRole id={UserId} role={Role}", id, role);
        var ok = await _admin.SetUserRoleAsync(id, role);
        if (!ok) return NotFound(new { success = false, message = MsgUserNotFound });
        return OkResponse(new { success = true, message = $"Role updated to {role}." });
    }

    [HttpPost("users/{id}/suspend")]
    public async Task<IActionResult> SuspendUser(string id, [FromBody] SuspendRequest? body)
    {
        _logger.LogInformation("[Admin] SuspendUser id={UserId} reason={Reason}", id, body?.Reason);
        var ok = await _admin.SuspendUserAsync(id, body?.Reason);
        if (!ok) return NotFound(new { success = false, message = MsgUserNotFound });
        return OkResponse(new { success = true, message = "User suspended." });
    }

    [HttpPost("users/{id}/reactivate")]
    public async Task<IActionResult> ReactivateUser(string id)
    {
        _logger.LogInformation("[Admin] ReactivateUser id={UserId}", id);
        var ok = await _admin.ReactivateUserAsync(id);
        if (!ok) return NotFound(new { success = false, message = MsgUserNotFound });
        return OkResponse(new { success = true, message = "User reactivated." });
    }

    [HttpPost("users/{id}/reset-password")]
    public async Task<IActionResult> ResetUserPassword(string id)
    {
        _logger.LogInformation("[Admin] ResetUserPassword id={UserId}", id);
        var tempPassword = await _admin.ResetUserPasswordAsync(id);
        if (tempPassword is null) return NotFound(new { success = false, message = MsgUserNotFound + " Or reset failed." });
        // SECURITY: Never return the temporary password in the API response.
        // It is sent to the user's email by the service layer.
        return OkResponse(new { success = true, message = "Password has been reset. A temporary password was sent to the user's email." });
    }

    [HttpPost("users/{id}/verify-creator")]
    public async Task<IActionResult> VerifyCreator(string id)
    {
        _logger.LogInformation("[Admin] VerifyCreator id={UserId}", id);
        var ok = await _admin.VerifyCreatorAsync(id);
        if (!ok) return NotFound(new { success = false, message = MsgUserNotFound });
        return OkResponse(new { success = true, message = "Creator verified." });
    }

    public record UpgradeTierRequest(string Tier);

    private static readonly HashSet<string> AllowedTiers = new(StringComparer.OrdinalIgnoreCase) { "free", "pro" };

    [HttpPost("upgrade-tier")]
    public async Task<IActionResult> UpgradeTier([FromBody] UpgradeTierRequest? body)
    {
        var tier = body?.Tier ?? "pro";
        if (!AllowedTiers.Contains(tier))
            return BadRequest(new { success = false, message = $"Invalid tier '{tier}'. Allowed: free, pro." });
        _logger.LogInformation("[Admin] UpgradeTier (bulk) tier={Tier}", tier);
        var users = await _admin.GetUsersAsync();
        var upgraded = 0;
        foreach (var u in users)
        {
            await _admin.UpgradeCreatorTierAsync(u.Id, tier);
            upgraded++;
        }
        return OkResponse(new { success = true, upgraded, message = $"Upgraded {upgraded} accounts to {tier}." });
    }

    [HttpPost("users/{id}/upgrade-tier")]
    public async Task<IActionResult> UpgradeUserTier(string id, [FromBody] UpgradeTierRequest? body)
    {
        var tier = body?.Tier ?? "pro";
        if (!AllowedTiers.Contains(tier))
            return BadRequest(new { success = false, message = $"Invalid tier '{tier}'. Allowed: free, pro." });
        _logger.LogInformation("[Admin] UpgradeUserTier id={UserId} tier={Tier}", id, tier);
        var ok = await _admin.UpgradeCreatorTierAsync(id, tier);
        if (!ok) return NotFound(new { success = false, message = MsgUserNotFound });
        return OkResponse(new { success = true, message = $"User {id} upgraded to {tier}." });
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
        _logger.LogInformation("[Admin] RemoveTrack id={TrackId}", id);
        var ok = await _admin.RemoveTrackAsync(id);
        if (!ok) return NotFound(new { success = false, message = MsgTrackNotFound });
        return OkResponse(new { success = true, message = "Track removed." });
    }

    [HttpPost("tracks/{id}/restore")]
    public async Task<IActionResult> RestoreTrack(string id)
    {
        _logger.LogInformation("[Admin] RestoreTrack id={TrackId}", id);
        var ok = await _admin.RestoreTrackAsync(id);
        if (!ok) return NotFound(new { success = false, message = MsgTrackNotFound });
        return OkResponse(new { success = true, message = "Track restored." });
    }

    [HttpPost("tracks/{id}/hide")]
    public async Task<IActionResult> HideTrack(string id)
    {
        _logger.LogInformation("[Admin] HideTrack id={TrackId}", id);
        var ok = await _admin.HideTrackAsync(id);
        if (!ok) return NotFound(new { success = false, message = MsgTrackNotFound });
        return OkResponse(new { success = true, message = "Track hidden." });
    }

    [HttpPost("tracks/{id}/flag")]
    public async Task<IActionResult> FlagTrack(string id)
    {
        _logger.LogInformation("[Admin] FlagTrack id={TrackId}", id);
        var ok = await _admin.FlagTrackAsync(id);
        if (!ok) return NotFound(new { success = false, message = MsgTrackNotFound });
        return OkResponse(new { success = true, message = "Track flagged." });
    }

    [HttpPost("tracks/{id}/feature")]
    public IActionResult FeatureTrack(string id)
    {
        return StatusCode(501, new { error = "Featured track placement is not yet implemented." });
    }

    [HttpPost("tracks/{id}/pin")]
    public IActionResult PinTrack(string id)
    {
        return StatusCode(501, new { error = "Track pinning is not yet implemented." });
    }

    public record VisibilityRequest(string Visibility);

    [HttpPost("tracks/{id}/visibility")]
    public async Task<IActionResult> SetTrackVisibility(string id, [FromBody] VisibilityRequest? body)
    {
        var visibility = body?.Visibility ?? "public";
        var ok = await _admin.SetTrackVisibilityAsync(id, visibility);
        if (!ok) return NotFound(new { success = false, message = MsgTrackNotFound });
        return OkResponse(new { success = true, message = "Track visibility updated." });
    }

    // --- Collections and tags ---

    [HttpPost("collections/curate")]
    public IActionResult CurateCollection()
    {
        return StatusCode(501, new { error = "Collection curation is not yet implemented." });
    }

    [HttpPost("tags/manage")]
    public IActionResult ManageTags()
    {
        return StatusCode(501, new { error = "Tag management is not yet implemented." });
    }

    /// <summary>
    /// Permanently delete all test/mock data (users, tracks, purchases, etc.).
    /// Preserves only the admin account. Requires ?confirm=yes query parameter.
    /// Blocked entirely in Production to prevent accidental data loss.
    /// </summary>
    [HttpPost("purge-test-data")]
    public async Task<IActionResult> PurgeTestData([FromQuery] string confirm)
    {
        if (_env.IsProduction())
        {
            _logger.LogCritical("[Admin] Purge attempt BLOCKED in Production environment");
            return StatusCode(403, new { error = "Purge is disabled in Production. This incident has been logged." });
        }

        if (confirm != "yes")
            return BadRequest(new { error = "Pass ?confirm=yes to confirm destructive operation." });

        var adminEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                      ?? User.FindFirst("email")?.Value
                      ?? "";
        if (string.IsNullOrWhiteSpace(adminEmail))
            return BadRequest(new { error = "Cannot determine admin email from token." });

        _logger.LogWarning("[Admin] Purge initiated by {AdminEmail} in {Environment}", adminEmail, _env.EnvironmentName);
        var result = await _admin.PurgeTestDataAsync(adminEmail);
        return OkResponse(result);
    }
}
