using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class LicenseCertificateRepository : ILicenseCertificateRepository
{
    private readonly CambrianDbContext _db;

    public LicenseCertificateRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<LicenseCertificate?> GetByIdAsync(Guid id)
    {
        return await _db.LicenseCertificates
            .Include(lc => lc.Buyer)
            .Include(lc => lc.Creator)
            .Include(lc => lc.Purchase)
            .FirstOrDefaultAsync(lc => lc.Id == id);
    }

    public async Task<LicenseCertificate?> GetByPurchaseIdAsync(Guid purchaseId)
    {
        return await _db.LicenseCertificates
            .Include(lc => lc.Buyer)
            .Include(lc => lc.Creator)
            .FirstOrDefaultAsync(lc => lc.PurchaseId == purchaseId);
    }

    public async Task<List<LicenseCertificate>> GetByBuyerIdAsync(string buyerId)
    {
        return await _db.LicenseCertificates
            .Where(lc => lc.BuyerId == buyerId)
            .OrderByDescending(lc => lc.IssuedAt)
            .ToListAsync();
    }

    public async Task<LicenseCertificate?> GetByBuyerAndTrackAsync(string buyerId, string cambrianTrackId)
    {
        return await _db.LicenseCertificates
            .Include(lc => lc.Buyer)
            .Include(lc => lc.Creator)
            .FirstOrDefaultAsync(lc => lc.BuyerId == buyerId && lc.TrackId == cambrianTrackId);
    }

    public async Task AddAsync(LicenseCertificate certificate)
    {
        _db.LicenseCertificates.Add(certificate);
        await _db.SaveChangesAsync();
    }
}
