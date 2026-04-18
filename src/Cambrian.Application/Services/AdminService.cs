using Cambrian.Application.DTOs.Admin;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class AdminService : IAdminService
{
    private readonly IAdminRepository _admin;

    public AdminService(IAdminRepository admin)
    {
        _admin = admin;
    }

    public async Task<AdminDashboardSummary> GetDashboardAsync()
    {
        var stats = await _admin.GetDashboardStatsAsync();
        return stats;
    }

    public async Task<IReadOnlyCollection<AdminAuditLog>> GetAuditLogsAsync()
    {
        return await _admin.GetAuditLogsAsync();
    }

    public async Task<IReadOnlyCollection<AdminUser>> GetUsersAsync()
    {
        return await _admin.GetUsersAsync();
    }

    public Task<IReadOnlyCollection<AdminTrack>> GetTracksAsync()
        => _admin.GetTracksAsync();

    public Task<IReadOnlyCollection<AdminPurchase>> GetPurchasesAsync()
        => _admin.GetPurchasesAsync();

    public Task<IReadOnlyCollection<AdminPayout>> GetPayoutsAsync()
        => _admin.GetPayoutsAsync();

    public async Task<PurgeResult> PurgeTestDataAsync(string adminEmail)
    {
        return await _admin.PurgeTestDataAsync(adminEmail);
    }

    // ── User management ──

    public Task<bool> SuspendUserAsync(string userId, string? reason, string adminActor)
        => _admin.SuspendUserAsync(userId, reason, adminActor);

    public Task<bool> ReactivateUserAsync(string userId, string adminActor)
        => _admin.ReactivateUserAsync(userId, adminActor);

    public Task<bool> SetUserRoleAsync(string userId, string role, string adminActor)
        => _admin.SetUserRoleAsync(userId, role, adminActor);

    public Task<bool> VerifyCreatorAsync(string userId, string adminActor)
        => _admin.VerifyCreatorAsync(userId, adminActor);

    public Task<bool> UpgradeCreatorTierAsync(string userId, string tier, string adminActor)
        => _admin.UpgradeCreatorTierAsync(userId, tier, adminActor);

    public Task<string?> ResetUserPasswordAsync(string userId, string adminActor)
        => _admin.ResetUserPasswordAsync(userId, adminActor);

    // ── Track moderation ──

    public Task<bool> RemoveTrackAsync(string trackId, string adminActor)
        => Guid.TryParse(trackId, out var id) ? _admin.RemoveTrackAsync(id, adminActor) : Task.FromResult(false);

    public Task<bool> RestoreTrackAsync(string trackId, string adminActor)
        => Guid.TryParse(trackId, out var id) ? _admin.RestoreTrackAsync(id, adminActor) : Task.FromResult(false);

    public Task<bool> HideTrackAsync(string trackId, string adminActor)
        => Guid.TryParse(trackId, out var id) ? _admin.HideTrackAsync(id, adminActor) : Task.FromResult(false);

    public Task<bool> FlagTrackAsync(string trackId, string adminActor)
        => Guid.TryParse(trackId, out var id) ? _admin.FlagTrackAsync(id, adminActor) : Task.FromResult(false);

    public Task<bool> SetTrackVisibilityAsync(string trackId, string visibility, string adminActor)
        => Guid.TryParse(trackId, out var id) ? _admin.SetTrackVisibilityAsync(id, visibility, adminActor) : Task.FromResult(false);
}
