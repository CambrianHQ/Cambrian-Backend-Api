using Cambrian.Api.Entities;

namespace Cambrian.Api.Services.Interfaces;

public interface IPayoutService
{
    Task RequestPayout(Guid creatorId, decimal amount);

    Task<IEnumerable<Payout>> GetHistory(Guid creatorId);
}
