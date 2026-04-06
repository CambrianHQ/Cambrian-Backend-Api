using Cambrian.Application.DTOs.Admin;

namespace Cambrian.Application.Interfaces;

public interface IAdminService
{
    Task<AdminDashboardSummary> GetDashboardAsync();

    Task<IReadOnlyCollection<AdminAuditLog>> GetAuditLogsAsync();

    Task<IReadOnlyCollection<AdminUser>> GetUsersAsync();

    Task<IReadOnlyCollection<AdminTrack>> GetTracksAsync();

    Task<IReadOnlyCollection<AdminPurchase>> GetPurchasesAsync();

    Task<IReadOnlyCollection<AdminPayout>> GetPayoutsAsync();

    Task<PurgeResult> PurgeTestDataAsync(string adminEmail);

    // ── User management ──
    Task<bool> SuspendUserAsync(string userId, string? reason);
    Task<bool> ReactivateUserAsync(string userId);
    Task<bool> SetUserRoleAsync(string userId, string role);
    Task<bool> VerifyCreatorAsync(string userId);
    Task<bool> UpgradeCreatorTierAsync(string userId, string tier);

    /// <summary>Returns the generated temporary password, or null if user not found.</summary>
    Task<string?> ResetUserPasswordAsync(string userId);

    // ── Payout management ──
    Task<bool> ApprovePayoutAsync(string payoutId);
    Task<bool> RejectPayoutAsync(string payoutId);

    // ── Track moderation ──
    Task<bool> RemoveTrackAsync(string trackId);
    Task<bool> RestoreTrackAsync(string trackId);
    Task<bool> HideTrackAsync(string trackId);
    Task<bool> FlagTrackAsync(string trackId);
    Task<bool> SetTrackVisibilityAsync(string trackId, string visibility);
}