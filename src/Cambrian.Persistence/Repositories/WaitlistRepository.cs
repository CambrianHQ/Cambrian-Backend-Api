using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class WaitlistRepository : IWaitlistRepository
{
    private readonly CambrianDbContext _db;

    public WaitlistRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public Task<WaitlistSignup?> GetByEmailAsync(string normalizedEmail)
    {
        return _db.WaitlistSignups
            .FirstOrDefaultAsync(s => s.Email == normalizedEmail);
    }

    public async Task AddAsync(WaitlistSignup signup)
    {
        _db.WaitlistSignups.Add(signup);
        await _db.SaveChangesAsync();
    }
}
