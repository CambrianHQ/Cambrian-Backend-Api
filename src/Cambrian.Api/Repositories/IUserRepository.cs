using Cambrian.Api.Entities;

namespace Cambrian.Api.Repositories;

public interface IUserRepository
{
    Task<User?> GetByEmail(string email);

    Task<User?> GetById(Guid id);

    Task Add(User user);
}
