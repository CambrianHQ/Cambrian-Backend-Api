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

    public async Task<PurgeResult> PurgeTestDataAsync(string adminEmail)
    {
        return await _admin.PurgeTestDataAsync(adminEmail);
    }
}