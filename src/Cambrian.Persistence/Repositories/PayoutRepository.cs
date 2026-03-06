using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class PayoutRepository : IPayoutRepository
{
    private readonly CambrianDbContext _db;

    public PayoutRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<List<Payout>> GetByCreatorIdAsync(string creatorId)
    {
        return await _db.Payouts
            .Where(p => p.CreatorId == creatorId)
            .OrderByDescending(p => p.RequestedAt)
            .ToListAsync();
    }

    public async Task<Payout?> GetByIdAsync(Guid id)
    {
        return await _db.Payouts
            .Include(p => p.Creator)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task AddAsync(Payout payout)
    {
        _db.Payouts.Add(payout);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Payout payout)
    {
        _db.Payouts.Update(payout);
        await _db.SaveChangesAsync();
    }
}
