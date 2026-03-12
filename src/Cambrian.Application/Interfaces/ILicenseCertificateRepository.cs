using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface ILicenseCertificateRepository
{
    Task<LicenseCertificate?> GetByIdAsync(Guid id);

    Task<LicenseCertificate?> GetByPurchaseIdAsync(Guid purchaseId);

    Task<List<LicenseCertificate>> GetByBuyerIdAsync(string buyerId);

    Task<LicenseCertificate?> GetByBuyerAndTrackAsync(string buyerId, string cambrianTrackId);

    Task AddAsync(LicenseCertificate certificate);
}
