using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class AdminService : IAdminService
{
    public Task<object> GetDashboardAsync()
    {
        return Task.FromResult<object>(new { users = 0, tracks = 0, revenue = 0m });
    }

    public Task<IReadOnlyCollection<object>> GetUsersAsync()
    {
        IReadOnlyCollection<object> users = [];
        return Task.FromResult(users);
    }
}