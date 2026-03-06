using Cambrian.Application.DTOs.Wallet;

namespace Cambrian.Application.Interfaces;

public interface IWalletService
{
    Task<WalletResponse> GetBalanceAsync(string userId);

    Task<IReadOnlyCollection<WalletTransactionResponse>> GetHistoryAsync(string userId, int take = 50);

    Task WithdrawAsync(double amount, string userId);
}
