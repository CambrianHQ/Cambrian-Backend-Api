using Cambrian.Application.DTOs.Admin;

namespace Cambrian.Application.Interfaces;

public interface IAdminRepository
{
    Task<AdminDashboardSummary> GetDashboardStatsAsync();

    Task<IReadOnlyCollection<AdminAuditLog>> GetAuditLogsAsync(int take = 200);

    Task<IReadOnlyCollection<AdminUser>> GetUsersAsync(int take = 500);

    Task<IReadOnlyCollection<AdminTrack>> GetTracksAsync(int take = 500);

    Task<IReadOnlyCollection<AdminPurchase>> GetPurchasesAsync(int take = 500);

    Task<IReadOnlyCollection<AdminPayout>> GetPayoutsAsync(int take = 500);

    /// <summary>
    /// Purge all test/mock data from the database, keeping only the admin account.
    /// </summary>
    Task<PurgeResult> PurgeTestDataAsync(string adminEmail);

    // ── User management ──
    Task<bool> SuspendUserAsync(string userId, string? reason, string adminActor);
    Task<bool> ReactivateUserAsync(string userId, string adminActor);
    Task<bool> SetUserRoleAsync(string userId, string role, string adminActor);
    Task<bool> VerifyCreatorAsync(string userId, string adminActor);
    Task<bool> UpgradeCreatorTierAsync(string userId, string tier, string adminActor);

    /// <summary>
    /// Admin-repair path for accounts stuck by a client/server username-onboarding desync.
    /// Delegates the actual validation/normalization/assignment to IUsernameOnboardingService
    /// (the same logic POST /auth/set-username uses) and records an audit log entry on success.
    /// </summary>
    Task<UsernameOnboardingResult> SetUsernameAsync(string userId, string username, string adminActor);

    /// <summary>
    /// Generates a random temporary password, resets the user's password via Identity, and returns the temp password.
    /// </summary>
    Task<string?> ResetUserPasswordAsync(string userId, string adminActor);

    // ── Track moderation ──
    Task<bool> RemoveTrackAsync(Guid trackId, string adminActor);
    Task<bool> RestoreTrackAsync(Guid trackId, string adminActor);
    Task<bool> HideTrackAsync(Guid trackId, string adminActor);
    Task<bool> FlagTrackAsync(Guid trackId, string adminActor);
    Task<bool> SetTrackVisibilityAsync(Guid trackId, string visibility, string adminActor);

    /// <summary>
    /// Admin-authorized permanent deletion: only valid for an already-trashed track
    /// (DeletedAt set). Queues the async object-storage purge (see TrackPurgeWorker) —
    /// never deletes the Track row itself, matching the owner-facing purge flow.
    /// </summary>
    Task<bool> PurgeTrackAsync(Guid trackId, string adminActor);

    // ── Track editorial placement (idempotent, one-way) ──
    Task<bool> FeatureTrackAsync(Guid trackId, string adminActor);
    Task<bool> PinTrackAsync(Guid trackId, string adminActor);

    // ── Payout review ──
    Task<PayoutReviewResult> ApprovePayoutAsync(Guid payoutId, string adminActor);
    Task<PayoutReviewResult> RejectPayoutAsync(Guid payoutId, string adminActor, string rejectionReason);

    // ── Reports / moderation ──
    Task<IReadOnlyCollection<AdminAbuseReport>> GetReportsAsync(int take = 500);
    Task<ReportActionResult> InvestigateReportAsync(Guid reportId, string adminActor);
    Task<ReportActionResult> CloseReportAsync(Guid reportId, string adminActor, string? resolutionNote);

    // ── Settings ──
    Task<AdminSettingsResponse> GetSettingsAsync();
    Task<AdminSettingsResponse> UpdateSettingsAsync(AdminSettingsUpdateRequest request, string adminActor);
}
