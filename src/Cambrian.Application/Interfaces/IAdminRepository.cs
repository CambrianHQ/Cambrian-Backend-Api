using Cambrian.Application.DTOs.Admin;

namespace Cambrian.Application.Interfaces;

public interface IAdminRepository
{
    Task<AdminDashboardSummary> GetDashboardStatsAsync();

    Task<IReadOnlyCollection<AdminAuditLog>> GetAuditLogsAsync(int take = 200);

    Task<IReadOnlyCollection<AdminUser>> GetUsersAsync(int take = 500);
}
