using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Admin;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
    private readonly StorageOptions _storageOptions;
    private readonly IObjectStorage _storage;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ICreatorIdentityRepository _creators;
    private readonly IFeatureFlagRepository _flags;
    private readonly IPaymentGateway _gateway;

    public AdminController(IAdminService admin, IMarketplaceIntegrityService integrity, ILogger<AdminController> logger, IWebHostEnvironment env, IOptions<StorageOptions> storageOptions, IObjectStorage storage, UserManager<ApplicationUser> users, ICreatorIdentityRepository creators, IFeatureFlagRepository flags, IPaymentGateway gateway)
    {
        _admin = admin;
        _integrity = integrity;
        _logger = logger;
        _env = env;
        _storageOptions = storageOptions.Value;
        _storage = storage;
        _users = users;
        _creators = creators;
        _flags = flags;
        _gateway = gateway;
    }

    private string GetAdminActor()
    {
        return User.FindFirstValue(ClaimTypes.Email)
            ?? User.FindFirstValue("email")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? "unknown";
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

    [HttpGet("integrations/stripe/status")]
    public async Task<IActionResult> StripeStatus()
    {
        var enabled = await _flags.IsEnabledAsync(StripeConnectAvailability.FeatureFlagName);

        return OkResponse(new
        {
            stripeConnectEnabled = enabled,
            ready = enabled,
            status = enabled ? "enabled" : "disabled",
            paymentGateway = _gateway.GetType().Name,
            checkedAt = DateTime.UtcNow
        });
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users()
    {
        var users = await _admin.GetUsersAsync();
        return OkResponse(users);
    }

    /// <summary>
    /// Diagnostic dump of an ApplicationUser plus its Creator row, exposing the
    /// raw fields the upload filters check (UserName vs Email, Role, CreatorTier,
    /// Creator.UserId match). Lets admins root-cause "why can't this user upload"
    /// without poking the DB directly.
    /// </summary>
    [HttpGet("users/{id}/diag")]
    public async Task<IActionResult> UserDiag(string id)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null) return NotFoundResponse(MsgUserNotFound);

        var creatorId = await _creators.GetCreatorIdForUserAsync(id);
        var usernameSet = UsernameHelper.IsSet(user);

        return OkResponse(new
        {
            id = user.Id,
            email = user.Email,
            userName = user.UserName,
            normalizedUserName = user.NormalizedUserName,
            displayName = user.DisplayName,
            role = user.Role,
            status = user.Status,
            tier = user.Tier,
            creatorTier = user.CreatorTier.ToString(),
            verifiedCreator = user.VerifiedCreator,
            uploadCount = user.UploadCount,
            emailConfirmed = user.EmailConfirmed,
            creatorRowId = creatorId,
            usernameHelperIsSet = usernameSet,
            requireUsernameWouldPass = usernameSet || creatorId.HasValue,
            requireCreatorTierWouldPass = string.Equals(user.Role, "Creator", StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase),
        });
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
    public async Task<IActionResult> GetSettings()
    {
        var settings = await _admin.GetSettingsAsync();
        return OkResponse(settings);
    }

    public record UpdateSettingsRequest(
        bool PayoutsEnabled,
        bool ModerationEnabled,
        bool MarketplaceEnabled,
        bool AllowExclusiveListings,
        bool RequireTrackReview);

    [HttpPost("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateSettingsRequest body)
    {
        _logger.LogInformation("[Admin] UpdateSettings by {Actor}", GetAdminActor());
        var updated = await _admin.UpdateSettingsAsync(new AdminSettingsUpdateRequest
        {
            PayoutsEnabled = body.PayoutsEnabled,
            ModerationEnabled = body.ModerationEnabled,
            MarketplaceEnabled = body.MarketplaceEnabled,
            AllowExclusiveListings = body.AllowExclusiveListings,
            RequireTrackReview = body.RequireTrackReview,
        }, GetAdminActor());
        return OkResponse(updated, "Settings saved.");
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
        _logger.LogInformation("[Admin] ApprovePayout id={PayoutId} by {Actor}", id, GetAdminActor());
        var result = await _admin.ApprovePayoutAsync(id, GetAdminActor());
        return result.Outcome switch
        {
            PayoutReviewOutcome.NotFound => NotFoundResponse(result.Message),
            PayoutReviewOutcome.InvalidState => ConflictResponse(result.Message),
            PayoutReviewOutcome.ApprovalRetryFailed => OkResponse(new { success = false, payout = result.Payout }, result.Message),
            _ => OkResponse(new { success = true, payout = result.Payout }, result.Message),
        };
    }

    public record RejectPayoutRequest(string Reason);

    [HttpPost("payouts/{id}/reject")]
    public async Task<IActionResult> RejectPayout(string id, [FromBody] RejectPayoutRequest? body)
    {
        if (string.IsNullOrWhiteSpace(body?.Reason))
            return ErrorResponse("A rejection reason is required.");

        _logger.LogInformation("[Admin] RejectPayout id={PayoutId} by {Actor}", id, GetAdminActor());
        var result = await _admin.RejectPayoutAsync(id, GetAdminActor(), body.Reason);
        return result.Outcome switch
        {
            PayoutReviewOutcome.NotFound => NotFoundResponse(result.Message),
            PayoutReviewOutcome.InvalidState => ConflictResponse(result.Message),
            _ => OkResponse(new { success = true, payout = result.Payout }, result.Message),
        };
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
        try
        {
            var ok = await _admin.SetUserRoleAsync(id, role, GetAdminActor());
            if (!ok) return NotFound(new { success = false, message = MsgUserNotFound });
            return OkResponse(new { success = true, message = $"Role updated to {role}." });
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("users/{id}/suspend")]
    public async Task<IActionResult> SuspendUser(string id, [FromBody] SuspendRequest? body)
    {
        _logger.LogInformation("[Admin] SuspendUser id={UserId} reason={Reason}", id, body?.Reason);
        var ok = await _admin.SuspendUserAsync(id, body?.Reason, GetAdminActor());
        if (!ok) return NotFound(new { success = false, message = MsgUserNotFound });
        return OkResponse(new { success = true, message = "User suspended." });
    }

    [HttpPost("users/{id}/reactivate")]
    public async Task<IActionResult> ReactivateUser(string id)
    {
        _logger.LogInformation("[Admin] ReactivateUser id={UserId}", id);
        var ok = await _admin.ReactivateUserAsync(id, GetAdminActor());
        if (!ok) return NotFound(new { success = false, message = MsgUserNotFound });
        return OkResponse(new { success = true, message = "User reactivated." });
    }

    [HttpPost("users/{id}/reset-password")]
    public async Task<IActionResult> ResetUserPassword(string id)
    {
        _logger.LogInformation("[Admin] ResetUserPassword id={UserId}", id);
        var tempPassword = await _admin.ResetUserPasswordAsync(id, GetAdminActor());
        if (tempPassword is null) return NotFound(new { success = false, message = MsgUserNotFound + " Or reset failed." });
        // SECURITY: Never return the temporary password in the API response.
        // It is sent to the user's email by the repository layer.
        return OkResponse(new { success = true, message = "Password has been reset. A temporary password was sent to the user's email." });
    }

    [HttpPost("users/{id}/verify-creator")]
    public async Task<IActionResult> VerifyCreator(string id)
    {
        _logger.LogInformation("[Admin] VerifyCreator id={UserId}", id);
        var ok = await _admin.VerifyCreatorAsync(id, GetAdminActor());
        if (!ok) return NotFound(new { success = false, message = MsgUserNotFound });
        // Return updated user so the frontend can refresh immediately
        var users = await _admin.GetUsersAsync();
        var updated = users.FirstOrDefault(u => u.Id == id);
        return OkResponse(new { success = true, message = "Creator verified and upgraded to Pro tier.", user = updated });
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
        var actor = GetAdminActor();
        var users = await _admin.GetUsersAsync();
        var upgraded = 0;
        foreach (var u in users)
        {
            await _admin.UpgradeCreatorTierAsync(u.Id, tier, actor);
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
        var ok = await _admin.UpgradeCreatorTierAsync(id, tier, GetAdminActor());
        if (!ok) return NotFound(new { success = false, message = MsgUserNotFound });
        // Return updated user so the frontend can refresh immediately
        var allUsers = await _admin.GetUsersAsync();
        var updatedUser = allUsers.FirstOrDefault(u => u.Id == id);
        return OkResponse(new { success = true, message = $"User {id} upgraded to {tier}.", user = updatedUser });
    }

    // --- Content moderation ---

    [HttpGet("reports")]
    public async Task<IActionResult> Reports()
    {
        var reports = await _admin.GetReportsAsync();
        return OkResponse(reports);
    }

    [HttpPost("reports/{id}/investigate")]
    public async Task<IActionResult> InvestigateReport(string id)
    {
        _logger.LogInformation("[Admin] InvestigateReport id={ReportId} by {Actor}", id, GetAdminActor());
        var result = await _admin.InvestigateReportAsync(id, GetAdminActor());
        return result.Outcome switch
        {
            ReportActionOutcome.NotFound => NotFoundResponse(result.Message),
            ReportActionOutcome.InvalidState => ConflictResponse(result.Message),
            _ => OkResponse(new { success = true, report = result.Report }, result.Message),
        };
    }

    public record CloseReportRequest(string? ResolutionNote);

    [HttpPost("reports/{id}/close")]
    public async Task<IActionResult> CloseReport(string id, [FromBody] CloseReportRequest? body)
    {
        _logger.LogInformation("[Admin] CloseReport id={ReportId} by {Actor}", id, GetAdminActor());
        var result = await _admin.CloseReportAsync(id, GetAdminActor(), body?.ResolutionNote);
        return result.Outcome switch
        {
            ReportActionOutcome.NotFound => NotFoundResponse(result.Message),
            ReportActionOutcome.InvalidState => ConflictResponse(result.Message),
            _ => OkResponse(new { success = true, report = result.Report }, result.Message),
        };
    }

    [HttpPost("tracks/{id}/remove")]
    public async Task<IActionResult> RemoveTrack(string id)
    {
        _logger.LogInformation("[Admin] RemoveTrack id={TrackId}", id);
        var ok = await _admin.RemoveTrackAsync(id, GetAdminActor());
        if (!ok) return NotFound(new { success = false, message = MsgTrackNotFound });
        return OkResponse(new { success = true, message = "Track removed." });
    }

    [HttpPost("tracks/{id}/restore")]
    public async Task<IActionResult> RestoreTrack(string id)
    {
        _logger.LogInformation("[Admin] RestoreTrack id={TrackId}", id);
        var ok = await _admin.RestoreTrackAsync(id, GetAdminActor());
        if (!ok) return NotFound(new { success = false, message = MsgTrackNotFound });
        return OkResponse(new { success = true, message = "Track restored." });
    }

    [HttpPost("tracks/{id}/purge")]
    public async Task<IActionResult> PurgeTrack(string id)
    {
        _logger.LogInformation("[Admin] PurgeTrack id={TrackId}", id);
        var ok = await _admin.PurgeTrackAsync(id, GetAdminActor());
        if (!ok) return ConflictResponse("Track must be in Trash (removed) before it can be permanently deleted.");
        return OkResponse(new { success = true, message = "Track queued for permanent deletion." });
    }

    [HttpPost("tracks/{id}/hide")]
    public async Task<IActionResult> HideTrack(string id)
    {
        _logger.LogInformation("[Admin] HideTrack id={TrackId}", id);
        var ok = await _admin.HideTrackAsync(id, GetAdminActor());
        if (!ok) return NotFound(new { success = false, message = MsgTrackNotFound });
        return OkResponse(new { success = true, message = "Track hidden." });
    }

    [HttpPost("tracks/{id}/flag")]
    public async Task<IActionResult> FlagTrack(string id)
    {
        _logger.LogInformation("[Admin] FlagTrack id={TrackId}", id);
        var ok = await _admin.FlagTrackAsync(id, GetAdminActor());
        if (!ok) return NotFound(new { success = false, message = MsgTrackNotFound });
        return OkResponse(new { success = true, message = "Track flagged." });
    }

    [HttpPost("tracks/{id}/feature")]
    public async Task<IActionResult> FeatureTrack(string id)
    {
        _logger.LogInformation("[Admin] FeatureTrack id={TrackId}", id);
        var ok = await _admin.FeatureTrackAsync(id, GetAdminActor());
        if (!ok) return NotFoundResponse(MsgTrackNotFound);
        return OkResponse(new { success = true, message = "Track featured." });
    }

    [HttpPost("tracks/{id}/pin")]
    public async Task<IActionResult> PinTrack(string id)
    {
        _logger.LogInformation("[Admin] PinTrack id={TrackId}", id);
        var ok = await _admin.PinTrackAsync(id, GetAdminActor());
        if (!ok) return NotFoundResponse(MsgTrackNotFound);
        return OkResponse(new { success = true, message = "Track pinned." });
    }

    private static readonly HashSet<string> AllowedVisibilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "hidden", "limited"
    };

    public record VisibilityRequest(string Visibility);

    [HttpPost("tracks/{id}/visibility")]
    public async Task<IActionResult> SetTrackVisibility(string id, [FromBody] VisibilityRequest? body)
    {
        var visibility = body?.Visibility ?? "public";
        if (!AllowedVisibilities.Contains(visibility))
            return BadRequest(new { success = false, message = $"Invalid visibility '{visibility}'. Allowed: public, hidden, limited." });
        var ok = await _admin.SetTrackVisibilityAsync(id, visibility, GetAdminActor());
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

    // --- Storage diagnostics ---

    /// <summary>
    /// Reports storage configuration status without exposing secrets.
    /// Also performs a live connectivity test (write + read + delete a tiny probe file).
    /// </summary>
    [HttpGet("storage-diagnostics")]
    public async Task<IActionResult> StorageDiagnostics()
    {
        var diag = new
        {
            provider = _storageOptions.Provider,
            endpoint = string.IsNullOrEmpty(_storageOptions.Endpoint) ? "NOT SET" : MaskValue(_storageOptions.Endpoint),
            bucket = string.IsNullOrEmpty(_storageOptions.Bucket) ? "NOT SET" : _storageOptions.Bucket,
            accessKey = string.IsNullOrEmpty(_storageOptions.AccessKey) ? "NOT SET" : MaskSecret(_storageOptions.AccessKey),
            secretKey = string.IsNullOrEmpty(_storageOptions.SecretKey) ? "NOT SET" : MaskSecret(_storageOptions.SecretKey),
            region = _storageOptions.Region,
            usePathStyle = _storageOptions.UsePathStyle,
            publicUrl = string.IsNullOrEmpty(_storageOptions.PublicUrl) ? "NOT SET" : _storageOptions.PublicUrl,
            localPath = _storageOptions.LocalPath,
            storageImpl = _storage.GetType().Name,
            configComplete = !string.IsNullOrEmpty(_storageOptions.Endpoint)
                          && !string.IsNullOrEmpty(_storageOptions.Bucket)
                          && !string.IsNullOrEmpty(_storageOptions.AccessKey)
                          && !string.IsNullOrEmpty(_storageOptions.SecretKey)
                          && !string.IsNullOrEmpty(_storageOptions.PublicUrl),
            connectivity = await TestStorageConnectivity()
        };
        return OkResponse(diag);
    }

    private async Task<object> TestStorageConnectivity()
    {
        var probeKey = $"_diagnostics/probe-{Guid.NewGuid():N}.txt";
        try
        {
            var content = System.Text.Encoding.UTF8.GetBytes("probe");
            using var stream = new MemoryStream(content);
            await _storage.UploadAsync(stream, probeKey, "text/plain");

            var readBack = await _storage.OpenReadAsync(probeKey);
            var canRead = readBack is not null;
            readBack?.Stream?.Dispose();

            await _storage.DeleteAsync(probeKey);

            return new { status = "ok", canWrite = true, canRead, probeKey };
        }
        catch (Exception ex)
        {
            return new { status = "error", canWrite = false, canRead = false, error = ex.Message, type = ex.GetType().Name };
        }
    }

    private static string MaskSecret(string value)
        => value.Length <= 6 ? "***" : value[..3] + "***" + value[^3..];

    private static string MaskValue(string value)
        => value.Length <= 10 ? "***" : value[..8] + "***";
}
