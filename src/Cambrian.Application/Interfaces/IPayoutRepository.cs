using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface IPayoutRepository
{
    Task<List<Payout>> GetByCreatorIdAsync(string creatorId);

    Task<Payout?> GetByIdAsync(Guid id);

    Task<Payout?> GetOutstandingAsync(string creatorId);

    Task AddAsync(Payout payout);

    Task UpdateAsync(Payout payout);
}
