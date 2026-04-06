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
    Task<bool> SuspendUserAsync(string userId, string? reason, string adminActor);
    Task<bool> ReactivateUserAsync(string userId, string adminActor);
    Task<bool> SetUserRoleAsync(string userId, string role, string adminActor);
    Task<bool> VerifyCreatorAsync(string userId, string adminActor);
    Task<bool> UpgradeCreatorTierAsync(string userId, string tier, string adminActor);

    /// <summary>Returns the generated temporary password, or null if user not found.</summary>
    Task<string?> ResetUserPasswordAsync(string userId, string adminActor);

    // ── Payout management ──
    Task<bool> ApprovePayoutAsync(string payoutId, string adminActor);
    Task<bool> RejectPayoutAsync(string payoutId, string adminActor);

    // ── Track moderation ──
    Task<bool> RemoveTrackAsync(string trackId, string adminActor);
    Task<bool> RestoreTrackAsync(string trackId, string adminActor);
    Task<bool> HideTrackAsync(string trackId, string adminActor);
    Task<bool> FlagTrackAsync(string trackId, string adminActor);
    Task<bool> SetTrackVisibilityAsync(string trackId, string visibility, string adminActor);
}
