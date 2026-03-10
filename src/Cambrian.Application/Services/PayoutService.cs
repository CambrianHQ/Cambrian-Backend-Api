using Cambrian.Application.DTOs.Payouts;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

public class PayoutService : IPayoutService
{
    private readonly IPayoutRepository _payouts;
    private readonly IPurchaseRepository _purchases;
    private readonly ITrackRepository _tracks;
    private readonly IPaymentGateway _gateway;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IWalletRepository _wallet;
    private readonly ILogger<PayoutService> _logger;

    public PayoutService(
        IPayoutRepository payouts,
        IPurchaseRepository purchases,
        ITrackRepository tracks,
        IPaymentGateway gateway,
        UserManager<ApplicationUser> users,
        IWalletRepository wallet,
        ILogger<PayoutService> logger)
    {
        _payouts = payouts;
        _purchases = purchases;
        _tracks = tracks;
        _gateway = gateway;
        _users = users;
        _wallet = wallet;
        _logger = logger;
    }

    public async Task<object> GetEarningsAsync(string userId)
    {
        // Compute real earnings from completed purchases on the creator's tracks
        var tracks = await _tracks.GetByCreatorIdAsync(userId);
        var allPurchases = new List<Purchase>();
        foreach (var track in tracks)
        {
            var tp = await _purchases.GetByTrackIdAsync(track.Id);
            allPurchases.AddRange(tp.Where(p => p.Status == "completed"));
        }

        var totalEarned = allPurchases.Sum(p => p.AmountCents) / 100m;

        var payouts = await _payouts.GetByCreatorIdAsync(userId);
        var paidOut = payouts.Where(p => p.Status == "completed").Sum(p => p.AmountCents) / 100m;
        var pendingPayouts = payouts.Where(p => p.Status == "pending").Sum(p => p.AmountCents) / 100m;
        var available = totalEarned - paidOut - pendingPayouts;

        return new
        {
            available = Math.Max(0, available),
            pending = pendingPayouts,
            totalEarned,
            totalWithdrawn = paidOut
        };
    }

    public async Task<PayoutResponse> RequestAsync(PayoutRequest request, string creatorId)
    {
        if (request.Amount <= 0)
            throw new ArgumentException("Amount must be greater than zero.");

        if (string.IsNullOrWhiteSpace(creatorId))
            throw new ArgumentException("Creator ID is required for payout requests.");

        // No minimum payout amount — any positive amount is valid

        // Verify creator has a connected Stripe account
        var user = await _users.FindByIdAsync(creatorId)
            ?? throw new KeyNotFoundException("Creator not found.");

        if (string.IsNullOrEmpty(user.StripeAccountId))
            throw new InvalidOperationException("You must connect a Stripe account before requesting a payout.");

        // Verify available wallet balance covers the request
        var balanceCents = await _wallet.GetBalanceAsync(creatorId);
        var requestCents = (long)Math.Round(request.Amount * 100, MidpointRounding.AwayFromZero);

        if (requestCents > balanceCents)
            throw new InvalidOperationException("Insufficient balance for this payout.");

        // Create pending payout record
        var payout = new Payout
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            AmountCents = (int)requestCents,
            Status = "pending",
            RequestedAt = DateTime.UtcNow
        };
        await _payouts.AddAsync(payout);

        // Debit the wallet
        await _wallet.AddTransactionAsync(new WalletTransaction
        {
            Id = Guid.NewGuid(),
            UserId = creatorId,
            AmountCents = -requestCents,
            Type = "withdrawal",
            Description = $"Payout ${request.Amount:F2}"
        });

        // Initiate Stripe transfer
        try
        {
            var transferId = await _gateway.CreateTransferAsync(
                user.StripeAccountId, requestCents,
                $"Cambrian payout {payout.Id}");

            payout.Status = "completed";
            payout.CompletedAt = DateTime.UtcNow;
            await _payouts.UpdateAsync(payout);

            _logger.LogInformation(
                "Payout {PayoutId} completed: {AmountCents}c → {AccountId} (transfer {TransferId})",
                payout.Id, requestCents, user.StripeAccountId, transferId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Payout {PayoutId} Stripe transfer failed — refunding wallet",
                payout.Id);

            // Refund the wallet debit
            await _wallet.AddTransactionAsync(new WalletTransaction
            {
                Id = Guid.NewGuid(),
                UserId = creatorId,
                AmountCents = requestCents,
                Type = "credit",
                Description = $"Payout refund (transfer failed): ${request.Amount:F2}"
            });

            payout.Status = "failed";
            await _payouts.UpdateAsync(payout);

            throw new InvalidOperationException(
                "Payout transfer failed. Your balance has been refunded. Please try again later.");
        }

        return new PayoutResponse
        {
            Amount = request.Amount,
            Status = payout.Status
        };
    }

    public async Task<IReadOnlyCollection<PayoutResponse>> GetHistoryAsync(string userId, int take = 50)
    {
        var payouts = await _payouts.GetByCreatorIdAsync(userId);

        return payouts.Take(take).Select(p => new PayoutResponse
        {
            Amount = p.AmountCents / 100m,
            Status = p.Status
        }).ToList();
    }
}