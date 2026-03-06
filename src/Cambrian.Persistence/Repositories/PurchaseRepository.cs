using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class PurchaseRepository : IPurchaseRepository
{
    private readonly CambrianDbContext _db;

    public PurchaseRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<Purchase?> GetByIdAsync(Guid id)
    {
        return await _db.Purchases
            .Include(p => p.Track)
            .Include(p => p.Buyer)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<List<Purchase>> GetByBuyerIdAsync(string buyerId)
    {
        return await _db.Purchases
            .Include(p => p.Track)
            .Where(p => p.BuyerId == buyerId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<Purchase>> GetByTrackIdAsync(Guid trackId)
    {
        return await _db.Purchases
            .Include(p => p.Buyer)
            .Where(p => p.TrackId == trackId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
    }

    public async Task AddAsync(Purchase purchase)
    {
        _db.Purchases.Add(purchase);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Purchase purchase)
    {
        _db.Purchases.Update(purchase);
        await _db.SaveChangesAsync();
    }
}