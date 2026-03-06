using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface IWalletRepository
{
    Task<long> GetBalanceAsync(string userId);

    Task<List<WalletTransaction>> GetHistoryAsync(string userId, int take = 50);

    Task AddTransactionAsync(WalletTransaction transaction);
}
