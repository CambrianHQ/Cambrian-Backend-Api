namespace Cambrian.Application.Interfaces;

public interface IAdminService
{
    Task<object> GetDashboardAsync();

    Task<IReadOnlyCollection<object>> GetUsersAsync();
}