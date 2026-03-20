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
    Task<bool> SuspendUserAsync(string userId, string? reason);
    Task<bool> ReactivateUserAsync(string userId);
    Task<bool> SetUserRoleAsync(string userId, string role);
    Task<bool> VerifyCreatorAsync(string userId);

    /// <summary>
    /// Generates a random temporary password, resets the user's password via Identity, and returns the temp password.
    /// </summary>
    Task<string?> ResetUserPasswordAsync(string userId);

    // ── Payout management ──
    Task<bool> ApprovePayoutAsync(Guid payoutId);
    Task<bool> RejectPayoutAsync(Guid payoutId);

    // ── Track moderation ──
    Task<bool> RemoveTrackAsync(Guid trackId);
    Task<bool> RestoreTrackAsync(Guid trackId);
    Task<bool> HideTrackAsync(Guid trackId);
    Task<bool> FlagTrackAsync(Guid trackId);
    Task<bool> SetTrackVisibilityAsync(Guid trackId, string visibility);
}
