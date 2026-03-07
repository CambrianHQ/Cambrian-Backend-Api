using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class InvoiceRepository : IInvoiceRepository
{
    private readonly CambrianDbContext _db;

    public InvoiceRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<List<Invoice>> GetByUserIdAsync(string userId)
    {
        return await _db.Invoices
            .Include(i => i.Purchase)
                .ThenInclude(p => p.Track)
            .Where(i => i.UserId == userId)
            .OrderByDescending(i => i.IssuedAt)
            .ToListAsync();
    }

    public async Task<Invoice?> GetByIdAsync(Guid id)
    {
        return await _db.Invoices
            .Include(i => i.Purchase)
                .ThenInclude(p => p.Track)
            .FirstOrDefaultAsync(i => i.Id == id);
    }

    public async Task AddAsync(Invoice invoice)
    {
        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Invoice invoice)
    {
        _db.Invoices.Update(invoice);
        await _db.SaveChangesAsync();
    }
}
