using Cambrian.Application.DTOs.Admin;

namespace Cambrian.Application.Interfaces;

public interface IAdminRepository
{
    Task<AdminDashboardSummary> GetDashboardStatsAsync();

    Task<IReadOnlyCollection<AdminAuditLog>> GetAuditLogsAsync(int take = 200);

    Task<IReadOnlyCollection<AdminUser>> GetUsersAsync(int take = 500);

    /// <summary>
    /// Purge all test/mock data from the database, keeping only the admin account.
    /// </summary>
    Task<PurgeResult> PurgeTestDataAsync(string adminEmail);
}
