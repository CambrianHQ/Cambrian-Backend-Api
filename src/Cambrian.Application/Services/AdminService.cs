using Cambrian.Application.DTOs.Admin;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace Cambrian.Application.Services;

public class AdminService : IAdminService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly ITrackRepository _tracks;
    private readonly IPurchaseRepository _purchases;
    private readonly IPayoutRepository _payouts;
    private readonly IAdminRepository _admin;

    public AdminService(
        UserManager<ApplicationUser> users,
        ITrackRepository tracks,
        IPurchaseRepository purchases,
        IPayoutRepository payouts,
        IAdminRepository admin)
    {
        _users = users;
        _tracks = tracks;
        _purchases = purchases;
        _payouts = payouts;
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

    public Task<bool> SuspendUserAsync(string userId, string? reason)
        => _admin.SuspendUserAsync(userId, reason);

    public Task<bool> ReactivateUserAsync(string userId)
        => _admin.ReactivateUserAsync(userId);

    public Task<bool> SetUserRoleAsync(string userId, string role)
        => _admin.SetUserRoleAsync(userId, role);

    public Task<bool> VerifyCreatorAsync(string userId)
        => _admin.VerifyCreatorAsync(userId);

    public Task<string?> ResetUserPasswordAsync(string userId)
        => _admin.ResetUserPasswordAsync(userId);

    // ── Payout management ──

    public Task<bool> ApprovePayoutAsync(string payoutId)
        => Guid.TryParse(payoutId, out var id) ? _admin.ApprovePayoutAsync(id) : Task.FromResult(false);

    public Task<bool> RejectPayoutAsync(string payoutId)
        => Guid.TryParse(payoutId, out var id) ? _admin.RejectPayoutAsync(id) : Task.FromResult(false);

    // ── Track moderation ──

    public Task<bool> RemoveTrackAsync(string trackId)
        => Guid.TryParse(trackId, out var id) ? _admin.RemoveTrackAsync(id) : Task.FromResult(false);

    public Task<bool> RestoreTrackAsync(string trackId)
        => Guid.TryParse(trackId, out var id) ? _admin.RestoreTrackAsync(id) : Task.FromResult(false);

    public Task<bool> HideTrackAsync(string trackId)
        => Guid.TryParse(trackId, out var id) ? _admin.HideTrackAsync(id) : Task.FromResult(false);

    public Task<bool> FlagTrackAsync(string trackId)
        => Guid.TryParse(trackId, out var id) ? _admin.FlagTrackAsync(id) : Task.FromResult(false);

    public Task<bool> SetTrackVisibilityAsync(string trackId, string visibility)
        => Guid.TryParse(trackId, out var id) ? _admin.SetTrackVisibilityAsync(id, visibility) : Task.FromResult(false);
}