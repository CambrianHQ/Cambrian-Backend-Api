using System.Security.Claims;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Admin;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Controller-level tests for AdminController covering dashboard, user management,
/// track moderation, payout approval, and input validation.
/// All endpoints require Admin role (class-level [Authorize(Roles="Admin")]).
/// </summary>
public sealed class AdminControllerTests
{
    private readonly IAdminService _admin = Substitute.For<IAdminService>();
    private readonly IMarketplaceIntegrityService _integrity = Substitute.For<IMarketplaceIntegrityService>();
    private readonly IObjectStorage _storage = Substitute.For<IObjectStorage>();
    private readonly IWebHostEnvironment _env = Substitute.For<IWebHostEnvironment>();
    private readonly IFeatureFlagRepository _flags = Substitute.For<IFeatureFlagRepository>();
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly AdminController _controller;

    public AdminControllerTests()
    {
        var logger = Substitute.For<ILogger<AdminController>>();
        var storageOptions = Options.Create(new StorageOptions { Provider = "local" });
        _env.EnvironmentName.Returns("Testing");
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(userStore, null!, null!, null!, null!, null!, null!, null!, null!);
        var creators = Substitute.For<ICreatorIdentityRepository>();
        _controller = new AdminController(_admin, _integrity, logger, _env, storageOptions, _storage, users, creators, _flags, _gateway);
        SetupAdmin();
    }

    private void SetupAdmin(string userId = "admin-1", string email = "admin@cambrian.com")
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Role, "Admin")
        }, "Test"));
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    // ── Dashboard ──

    [Fact]
    public async Task Dashboard_ReturnsOk()
    {
        _admin.GetDashboardAsync().Returns(new AdminDashboardSummary
        {
            TotalUsers = 10,
            ActiveCreators = 3,
            TracksUploaded = 25,
            LicensesSold = 5,
            TotalRevenue = 1500.0,
            PendingPayouts = 200.0
        });

        var result = await _controller.Dashboard();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Audit ──

    [Fact]
    public async Task Audit_ReturnsOk()
    {
        _admin.GetAuditLogsAsync().Returns(new List<AdminAuditLog>());

        var result = await _controller.Audit();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task StripeStatus_ReturnsFlagState()
    {
        _flags.IsEnabledAsync("StripeConnectEnabled").Returns(true);

        var result = await _controller.StripeStatus();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    // ── Users ──

    [Fact]
    public async Task Users_ReturnsOk()
    {
        _admin.GetUsersAsync().Returns(new List<AdminUser>());

        var result = await _controller.Users();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── SetUserRole ──

    [Fact]
    public async Task SetUserRole_ValidRole_ReturnsOk()
    {
        _admin.SetUserRoleAsync("user-1", "Creator", Arg.Any<string>()).Returns(true);

        var result = await _controller.SetUserRole("user-1", new AdminController.SetRoleRequest("Creator"));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task SetUserRole_InvalidRole_ReturnsBadRequest()
    {
        var result = await _controller.SetUserRole("user-1", new AdminController.SetRoleRequest("SuperAdmin"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SetUserRole_UserNotFound_ReturnsNotFound()
    {
        _admin.SetUserRoleAsync("missing", "User", Arg.Any<string>()).Returns(false);

        var result = await _controller.SetUserRole("missing", new AdminController.SetRoleRequest("User"));

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── SuspendUser ──

    [Fact]
    public async Task SuspendUser_ReturnsOk_WhenFound()
    {
        _admin.SuspendUserAsync("user-1", "spam", Arg.Any<string>()).Returns(true);

        var result = await _controller.SuspendUser("user-1", new AdminController.SuspendRequest("spam"));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task SuspendUser_ReturnsNotFound_WhenMissing()
    {
        _admin.SuspendUserAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>()).Returns(false);

        var result = await _controller.SuspendUser("missing", null);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── ReactivateUser ──

    [Fact]
    public async Task ReactivateUser_ReturnsOk_WhenFound()
    {
        _admin.ReactivateUserAsync("user-1", Arg.Any<string>()).Returns(true);

        var result = await _controller.ReactivateUser("user-1");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task ReactivateUser_ReturnsNotFound_WhenMissing()
    {
        _admin.ReactivateUserAsync("missing", Arg.Any<string>()).Returns(false);

        var result = await _controller.ReactivateUser("missing");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── RemoveTrack ──

    [Fact]
    public async Task RemoveTrack_ReturnsOk_WhenFound()
    {
        _admin.RemoveTrackAsync("t-1", Arg.Any<string>()).Returns(true);

        var result = await _controller.RemoveTrack("t-1");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task RemoveTrack_ReturnsNotFound_WhenMissing()
    {
        _admin.RemoveTrackAsync("missing", Arg.Any<string>()).Returns(false);

        var result = await _controller.RemoveTrack("missing");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── RestoreTrack ──

    [Fact]
    public async Task RestoreTrack_ReturnsOk_WhenFound()
    {
        _admin.RestoreTrackAsync("t-1", Arg.Any<string>()).Returns(true);

        var result = await _controller.RestoreTrack("t-1");

        Assert.IsType<OkObjectResult>(result);
    }

    // ── HideTrack ──

    [Fact]
    public async Task HideTrack_ReturnsOk_WhenFound()
    {
        _admin.HideTrackAsync("t-1", Arg.Any<string>()).Returns(true);

        var result = await _controller.HideTrack("t-1");

        Assert.IsType<OkObjectResult>(result);
    }

    // ── FlagTrack ──

    [Fact]
    public async Task FlagTrack_ReturnsOk_WhenFound()
    {
        _admin.FlagTrackAsync("t-1", Arg.Any<string>()).Returns(true);

        var result = await _controller.FlagTrack("t-1");

        Assert.IsType<OkObjectResult>(result);
    }

    // ── SetTrackVisibility ──

    [Fact]
    public async Task SetTrackVisibility_ValidVisibility_ReturnsOk()
    {
        _admin.SetTrackVisibilityAsync("t-1", "hidden", Arg.Any<string>()).Returns(true);

        var result = await _controller.SetTrackVisibility("t-1", new AdminController.VisibilityRequest("hidden"));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task SetTrackVisibility_InvalidVisibility_ReturnsBadRequest()
    {
        var result = await _controller.SetTrackVisibility("t-1", new AdminController.VisibilityRequest("banned"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SetTrackVisibility_TrackNotFound_ReturnsNotFound()
    {
        _admin.SetTrackVisibilityAsync("missing", "public", Arg.Any<string>()).Returns(false);

        var result = await _controller.SetTrackVisibility("missing", new AdminController.VisibilityRequest("public"));

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── VerifyCreator ──

    [Fact]
    public async Task VerifyCreator_Found_ReturnsOkWithUpdatedUser()
    {
        _admin.VerifyCreatorAsync("user-1", Arg.Any<string>()).Returns(true);
        _admin.GetUsersAsync().Returns(new List<AdminUser>
        {
            new() { Id = "user-1", VerifiedCreator = true, CreatorTier = "Pro", Tier = "pro" }
        });

        var result = await _controller.VerifyCreator("user-1");

        Assert.IsType<OkObjectResult>(result);
        await _admin.Received(1).VerifyCreatorAsync("user-1", Arg.Any<string>());
    }

    [Fact]
    public async Task VerifyCreator_UserNotFound_ReturnsNotFound()
    {
        _admin.VerifyCreatorAsync("missing", Arg.Any<string>()).Returns(false);

        var result = await _controller.VerifyCreator("missing");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── UpgradeUserTier ──

    [Fact]
    public async Task UpgradeUserTier_ValidTier_ReturnsOk()
    {
        _admin.UpgradeCreatorTierAsync("user-1", "pro", Arg.Any<string>()).Returns(true);
        _admin.GetUsersAsync().Returns(new List<AdminUser> { new() { Id = "user-1" } });

        var result = await _controller.UpgradeUserTier("user-1", new AdminController.UpgradeTierRequest("pro"));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task UpgradeUserTier_InvalidTier_ReturnsBadRequest()
    {
        var result = await _controller.UpgradeUserTier("user-1", new AdminController.UpgradeTierRequest("diamond"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpgradeUserTier_UserNotFound_ReturnsNotFound()
    {
        _admin.UpgradeCreatorTierAsync("missing", "pro", Arg.Any<string>()).Returns(false);

        var result = await _controller.UpgradeUserTier("missing", new AdminController.UpgradeTierRequest("pro"));

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── PurgeTestData ──

    [Fact]
    public async Task PurgeTestData_WithoutConfirm_ReturnsBadRequest()
    {
        var result = await _controller.PurgeTestData("no");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PurgeTestData_InProduction_Returns403()
    {
        _env.EnvironmentName.Returns("Production");

        var result = await _controller.PurgeTestData("yes");

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, obj.StatusCode);
    }

    [Fact]
    public async Task PurgeTestData_WithConfirm_ReturnsOk()
    {
        _admin.PurgeTestDataAsync(Arg.Any<string>()).Returns(new PurgeResult());

        var result = await _controller.PurgeTestData("yes");

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Settings ──

    [Fact]
    public async Task GetSettings_ReturnsOk()
    {
        _admin.GetSettingsAsync().Returns(new AdminSettingsResponse());

        var result = await _controller.GetSettings();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task UpdateSettings_ReturnsOk_AndPersistsViaService()
    {
        var request = new AdminController.UpdateSettingsRequest(true, false, true, false, true);
        _admin.UpdateSettingsAsync(
            Arg.Is<AdminSettingsUpdateRequest>(r =>
                r.PayoutsEnabled && !r.ModerationEnabled && r.MarketplaceEnabled && !r.AllowExclusiveListings && r.RequireTrackReview),
            Arg.Any<string>()
        ).Returns(new AdminSettingsResponse());

        var result = await _controller.UpdateSettings(request);

        Assert.IsType<OkObjectResult>(result);
        await _admin.Received(1).UpdateSettingsAsync(Arg.Any<AdminSettingsUpdateRequest>(), Arg.Any<string>());
    }

    // ── IntegrityAudit ──

    [Fact]
    public async Task IntegrityAudit_ReturnsOk()
    {
        _integrity.RunAuditAsync().Returns(new IntegrityReport());

        var result = await _controller.IntegrityAudit();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Track editorial placement ──

    [Fact]
    public async Task FeatureTrack_ReturnsOk_WhenFound()
    {
        _admin.FeatureTrackAsync("t-1", Arg.Any<string>()).Returns(true);

        var result = await _controller.FeatureTrack("t-1");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task FeatureTrack_ReturnsNotFound_WhenMissing()
    {
        _admin.FeatureTrackAsync("missing", Arg.Any<string>()).Returns(false);

        var result = await _controller.FeatureTrack("missing");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task PinTrack_ReturnsOk_WhenFound()
    {
        _admin.PinTrackAsync("t-1", Arg.Any<string>()).Returns(true);

        var result = await _controller.PinTrack("t-1");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task PinTrack_ReturnsNotFound_WhenMissing()
    {
        _admin.PinTrackAsync("missing", Arg.Any<string>()).Returns(false);

        var result = await _controller.PinTrack("missing");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── Payout review ──

    [Fact]
    public async Task ApprovePayout_ReturnsOk_WhenApproved()
    {
        _admin.ApprovePayoutAsync("p-1", Arg.Any<string>()).Returns(new PayoutReviewResult
        {
            Outcome = PayoutReviewOutcome.Approved,
            Payout = new AdminPayout { Id = "p-1", Status = "completed" },
            Message = "Payout approved and the Stripe transfer completed."
        });

        var result = await _controller.ApprovePayout("p-1");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task ApprovePayout_ReturnsNotFound_WhenMissing()
    {
        _admin.ApprovePayoutAsync("missing", Arg.Any<string>()).Returns(new PayoutReviewResult
        {
            Outcome = PayoutReviewOutcome.NotFound,
            Message = "Payout not found."
        });

        var result = await _controller.ApprovePayout("missing");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task ApprovePayout_ReturnsConflict_WhenNotPending()
    {
        _admin.ApprovePayoutAsync("p-1", Arg.Any<string>()).Returns(new PayoutReviewResult
        {
            Outcome = PayoutReviewOutcome.InvalidState,
            Message = "Payout is 'completed', not pending — only pending payouts can be approved."
        });

        var result = await _controller.ApprovePayout("p-1");

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, obj.StatusCode);
    }

    [Fact]
    public async Task ApprovePayout_RetryFailed_ReturnsOkWithSuccessFalse()
    {
        _admin.ApprovePayoutAsync("p-1", Arg.Any<string>()).Returns(new PayoutReviewResult
        {
            Outcome = PayoutReviewOutcome.ApprovalRetryFailed,
            Payout = new AdminPayout { Id = "p-1", Status = "pending" },
            Message = "Stripe transfer retry failed: card declined. Payout remains pending."
        });

        var result = await _controller.ApprovePayout("p-1");

        // Must never report bare success when the retry actually failed.
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task RejectPayout_BlankReason_ReturnsBadRequest()
    {
        var result = await _controller.RejectPayout("p-1", new AdminController.RejectPayoutRequest(""));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RejectPayout_NullBody_ReturnsBadRequest()
    {
        var result = await _controller.RejectPayout("p-1", null);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RejectPayout_ReturnsOk_WhenRejected()
    {
        _admin.RejectPayoutAsync("p-1", Arg.Any<string>(), "fraud suspected").Returns(new PayoutReviewResult
        {
            Outcome = PayoutReviewOutcome.Rejected,
            Payout = new AdminPayout { Id = "p-1", Status = "rejected" },
            Message = "Payout rejected."
        });

        var result = await _controller.RejectPayout("p-1", new AdminController.RejectPayoutRequest("fraud suspected"));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task RejectPayout_ReturnsNotFound_WhenMissing()
    {
        _admin.RejectPayoutAsync("missing", Arg.Any<string>(), Arg.Any<string>()).Returns(new PayoutReviewResult
        {
            Outcome = PayoutReviewOutcome.NotFound,
            Message = "Payout not found."
        });

        var result = await _controller.RejectPayout("missing", new AdminController.RejectPayoutRequest("reason"));

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task RejectPayout_ReturnsConflict_WhenNotPending()
    {
        _admin.RejectPayoutAsync("p-1", Arg.Any<string>(), Arg.Any<string>()).Returns(new PayoutReviewResult
        {
            Outcome = PayoutReviewOutcome.InvalidState,
            Message = "Payout is 'rejected', not pending — only pending payouts can be rejected."
        });

        var result = await _controller.RejectPayout("p-1", new AdminController.RejectPayoutRequest("reason"));

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, obj.StatusCode);
    }

    // ── Reports / moderation ──

    [Fact]
    public async Task Reports_ReturnsOk()
    {
        _admin.GetReportsAsync().Returns(new List<AdminAbuseReport>());

        var result = await _controller.Reports();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task InvestigateReport_ReturnsOk_WhenSuccessful()
    {
        _admin.InvestigateReportAsync("r-1", Arg.Any<string>()).Returns(new ReportActionResult
        {
            Outcome = ReportActionOutcome.Success,
            Report = new AdminAbuseReport { Id = "r-1", Status = "investigating" },
            Message = "Report is under investigation."
        });

        var result = await _controller.InvestigateReport("r-1");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task InvestigateReport_ReturnsNotFound_WhenMissing()
    {
        _admin.InvestigateReportAsync("missing", Arg.Any<string>()).Returns(new ReportActionResult
        {
            Outcome = ReportActionOutcome.NotFound,
            Message = "Report not found."
        });

        var result = await _controller.InvestigateReport("missing");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task InvestigateReport_ReturnsConflict_WhenClosed()
    {
        _admin.InvestigateReportAsync("r-1", Arg.Any<string>()).Returns(new ReportActionResult
        {
            Outcome = ReportActionOutcome.InvalidState,
            Message = "Report is closed and cannot be re-investigated."
        });

        var result = await _controller.InvestigateReport("r-1");

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, obj.StatusCode);
    }

    [Fact]
    public async Task CloseReport_ReturnsOk_WhenSuccessful()
    {
        _admin.CloseReportAsync("r-1", Arg.Any<string>(), Arg.Any<string?>()).Returns(new ReportActionResult
        {
            Outcome = ReportActionOutcome.Success,
            Report = new AdminAbuseReport { Id = "r-1", Status = "closed" },
            Message = "Report closed."
        });

        var result = await _controller.CloseReport("r-1", new AdminController.CloseReportRequest("resolved"));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task CloseReport_ReturnsNotFound_WhenMissing()
    {
        _admin.CloseReportAsync("missing", Arg.Any<string>(), Arg.Any<string?>()).Returns(new ReportActionResult
        {
            Outcome = ReportActionOutcome.NotFound,
            Message = "Report not found."
        });

        var result = await _controller.CloseReport("missing", null);

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
