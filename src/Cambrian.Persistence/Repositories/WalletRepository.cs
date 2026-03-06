using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class WalletRepository : IWalletRepository
{
    private readonly CambrianDbContext _db;

    public WalletRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<long> GetBalanceAsync(string userId)
    {
        return await _db.WalletTransactions
            .Where(w => w.UserId == userId)
            .SumAsync(w => w.AmountCents);
    }

    public async Task<List<WalletTransaction>> GetHistoryAsync(string userId, int take = 50)
    {
        return await _db.WalletTransactions
            .Where(w => w.UserId == userId)
            .OrderByDescending(w => w.CreatedAt)
            .Take(take)
            .ToListAsync();
    }

    public async Task AddTransactionAsync(WalletTransaction transaction)
    {
        _db.WalletTransactions.Add(transaction);
        await _db.SaveChangesAsync();
    }
}
