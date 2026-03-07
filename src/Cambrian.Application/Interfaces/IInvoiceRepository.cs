using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface IInvoiceRepository
{
    Task<List<Invoice>> GetByUserIdAsync(string userId);

    Task<Invoice?> GetByIdAsync(Guid id);

    Task AddAsync(Invoice invoice);

    Task UpdateAsync(Invoice invoice);
}
