using Cambrian.Api.Entities;

namespace Cambrian.Api.Services.Interfaces;

public interface IAdminService
{
    Task<IEnumerable<User>> GetUsers();

    Task SuspendUser(Guid id);
}
