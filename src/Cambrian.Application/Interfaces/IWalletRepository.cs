using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface IWalletRepository
{
    Task<long> GetBalanceAsync(string userId);

    Task<List<WalletTransaction>> GetHistoryAsync(string userId, int take = 50);

    Task AddTransactionAsync(WalletTransaction transaction);

    /// <summary>
    /// Atomically check balance and create a withdrawal transaction in a single
    /// serializable transaction to prevent double-withdrawal race conditions.
    /// Returns true if the withdrawal succeeded, false if balance was insufficient.
    /// </summary>
    Task<bool> AtomicWithdrawAsync(string userId, long amountCents, string description);

    Task<long> GetTotalCreditsAsync(string userId);

    Task<long> GetCreditsAfterAsync(string userId, DateTime after);

    Task<Dictionary<Guid, long>> GetCreditsByTrackAsync(string userId, IEnumerable<Guid> trackIds);
}
