using Cambrian.Application.DTOs.Wallet;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

public sealed class WalletService : IWalletService
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
        var balanceCents = await _wallet.GetBalanceAsync(userId);
        var amountCents = (long)(amount * 100);

        if (amountCents > balanceCents)
            throw new InvalidOperationException("Insufficient balance.");

        var txn = new WalletTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AmountCents = -amountCents,
            Type = "withdrawal",
            Description = $"Withdrawal of ${amount:F2}"
        };

        await _wallet.AddTransactionAsync(txn);
    }
}
