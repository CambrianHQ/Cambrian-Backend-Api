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

    public async Task<bool> AtomicWithdrawAsync(string userId, long amountCents, string description)
    {
        // Use a serializable transaction to prevent double-withdrawal race conditions.
        // The balance check and insertion happen atomically.
        await using var transaction = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable);
        try
        {
            var balance = await _db.WalletTransactions
                .Where(w => w.UserId == userId)
                .SumAsync(w => w.AmountCents);

            if (amountCents > balance)
            {
                await transaction.RollbackAsync();
                return false;
            }

            _db.WalletTransactions.Add(new WalletTransaction
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AmountCents = -amountCents,
                Type = "withdrawal",
                Description = description
            });

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return true;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<long> GetTotalCreditsAsync(string userId)
    {
        return await _db.WalletTransactions
            .Where(w => w.UserId == userId && w.Type == "credit")
            .SumAsync(w => w.AmountCents);
    }

    public async Task<long> GetCreditsAfterAsync(string userId, DateTime after)
    {
        return await _db.WalletTransactions
            .Where(w => w.UserId == userId && w.Type == "credit" && w.CreatedAt >= after)
            .SumAsync(w => w.AmountCents);
    }

    public async Task<Dictionary<Guid, long>> GetCreditsByTrackAsync(string userId, IEnumerable<Guid> trackIds)
    {
        var trackIdSet = trackIds.ToHashSet();
        return await _db.WalletTransactions
            .Where(w => w.UserId == userId && w.Type == "credit" && w.RelatedPurchaseId != null)
            .Join(_db.Purchases,
                wt => wt.RelatedPurchaseId,
                p => p.Id,
                (wt, p) => new { p.TrackId, wt.AmountCents })
            .Where(x => trackIdSet.Contains(x.TrackId))
            .GroupBy(x => x.TrackId)
            .Select(g => new { TrackId = g.Key, Total = g.Sum(x => x.AmountCents) })
            .ToDictionaryAsync(x => x.TrackId, x => x.Total);
    }
}
