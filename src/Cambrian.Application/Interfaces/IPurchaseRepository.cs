using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface IPurchaseRepository
{
    Task<Purchase?> GetByIdAsync(Guid id);

    Task<List<Purchase>> GetByBuyerIdAsync(string buyerId);

    Task<List<Purchase>> GetByTrackIdAsync(Guid trackId);

    Task AddAsync(Purchase purchase);

    Task UpdateAsync(Purchase purchase);
}
