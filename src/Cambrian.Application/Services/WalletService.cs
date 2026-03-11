using Cambrian.Application.DTOs.Wallet;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

public class WalletService : IWalletService
{
    private readonly IWalletRepository _wallet;

    public WalletService(IWalletRepository wallet)
    {
        _wallet = wallet;
    }

    public async Task<WalletResponse> GetBalanceAsync(string userId)
    {
        var balanceCents = await _wallet.GetBalanceAsync(userId);
        return new WalletResponse
        {
            BalanceCents = balanceCents,
            Currency = "usd"
        };
    }

    public async Task<IReadOnlyCollection<WalletTransactionResponse>> GetHistoryAsync(string userId, int take = 50)
    {
        var transactions = await _wallet.GetHistoryAsync(userId, take);

        return transactions.Select(t => new WalletTransactionResponse
        {
            Id = t.Id.ToString(),
            AmountCents = t.AmountCents,
            Type = t.Type,
            Description = t.Description,
            CreatedAt = t.CreatedAt
        }).ToList();
    }

    public async Task WithdrawAsync(double amount, string userId)
    {
        if (amount <= 0)
            throw new ArgumentException("Withdrawal amount must be greater than zero.");

        var amountCents = (long)Math.Round(amount * 100, MidpointRounding.AwayFromZero);

        if (amountCents <= 0)
            throw new ArgumentException("Withdrawal amount must be greater than zero.");

        // Use atomic withdraw to prevent race condition (double-withdrawal)
        var success = await _wallet.AtomicWithdrawAsync(userId, amountCents, $"Withdrawal of ${amount:F2}");

        if (!success)
            throw new InvalidOperationException("Insufficient balance.");
    }
}
