using Cambrian.Application.DTOs.Admin;

namespace Cambrian.Application.Interfaces;

public interface IAdminService
{
    Task<AdminDashboardSummary> GetDashboardAsync();

    Task<IReadOnlyCollection<AdminAuditLog>> GetAuditLogsAsync();

    Task<IReadOnlyCollection<AdminUser>> GetUsersAsync();

    Task<PurgeResult> PurgeTestDataAsync(string adminEmail);
}