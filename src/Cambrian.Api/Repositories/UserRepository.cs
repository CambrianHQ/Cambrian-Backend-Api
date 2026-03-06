using Cambrian.Api.Data;
using Cambrian.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.Repositories;

public class UserRepository : IUserRepository
{
    private readonly ApplicationDbContext _db;

    public UserRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<User?> GetByEmail(string email)
    {
        return await _db.Users.FirstOrDefaultAsync(x => x.Email == email);
    }

    public async Task<User?> GetById(Guid id)
    {
        return await _db.Users.FindAsync(id);
    }

    public async Task Add(User user)
    {
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
    }
}
