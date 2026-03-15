using Cambrian.Application.DTOs.Admin;

namespace Cambrian.Application.Interfaces;

public interface IAdminService
{
    Task<AdminDashboardSummary> GetDashboardAsync();

    Task<IReadOnlyCollection<AdminAuditLog>> GetAuditLogsAsync();

    Task<IReadOnlyCollection<AdminUser>> GetUsersAsync();

    Task<PurgeResult> PurgeTestDataAsync(string adminEmail);

    // ── User management ──
    Task<bool> SuspendUserAsync(string userId, string? reason);
    Task<bool> ReactivateUserAsync(string userId);
    Task<bool> SetUserRoleAsync(string userId, string role);
    Task<bool> VerifyCreatorAsync(string userId);

    // ── Track moderation ──
    Task<bool> RemoveTrackAsync(string trackId);
    Task<bool> RestoreTrackAsync(string trackId);
    Task<bool> HideTrackAsync(string trackId);
    Task<bool> FlagTrackAsync(string trackId);
    Task<bool> SetTrackVisibilityAsync(string trackId, string visibility);
}