using Cambrian.Application.DTOs.Admin;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class AdminService : IAdminService
{
    public Task<AdminDashboardSummary> GetDashboardAsync()
    {
        var summary = new AdminDashboardSummary
        {
            TotalUsers = 0,
            ActiveCreators = 0,
            TracksUploaded = 0,
            LicensesSold = 0,
            TotalRevenue = 0,
            PendingPayouts = 0
        };

        return Task.FromResult(summary);
    }

    public Task<IReadOnlyCollection<AdminAuditLog>> GetAuditLogsAsync()
    {
        IReadOnlyCollection<AdminAuditLog> logs = [];
        return Task.FromResult(logs);
    }

    public Task<IReadOnlyCollection<AdminUser>> GetUsersAsync()
    {
        IReadOnlyCollection<AdminUser> users = [];
        return Task.FromResult(users);
    }
}