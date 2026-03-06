using Cambrian.Api.Data;
using Cambrian.Api.Entities;
using Cambrian.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.Services;

public class AdminService : IAdminService
{
    private readonly ApplicationDbContext _db;

    public AdminService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IEnumerable<User>> GetUsers()
    {
        return await _db.Users.ToListAsync();
    }

    public async Task SuspendUser(Guid id)
    {
        var user = await _db.Users.FindAsync(id);

        if (user != null)
        {
            user.Role = "Suspended";
            await _db.SaveChangesAsync();
        }
    }
}
