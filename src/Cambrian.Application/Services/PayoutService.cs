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
    private readonly ITransactionManager _transactions;
    private readonly ICreatorIdentityRepository _creators;
    private readonly ILogger<PayoutService> _logger;

    public PayoutService(
        IPayoutRepository payouts,
        IPurchaseRepository purchases,
        ITrackRepository tracks,
        IPaymentGateway gateway,
        UserManager<ApplicationUser> users,
        IWalletRepository wallet,
        ITransactionManager transactions,
        ICreatorIdentityRepository creators,
        ILogger<PayoutService> logger)
    {
        _payouts = payouts;
        _purchases = purchases;
        _tracks = tracks;
        _gateway = gateway;
        _users = users;
        _wallet = wallet;
        _transactions = transactions;
        _creators = creators;
        _logger = logger;
    }

    public async Task<object> GetEarningsAsync(string userId)
    {
        // Resolve the creator's tier-based fee rate instead of using a hardcoded value
        var user = await _users.FindByIdAsync(userId);
        var platformFeeRate = user is not null
            ? Configuration.TierManifest.For(user.CreatorTier).FeeRate
            : Configuration.TierManifest.Free.FeeRate;

        // Compute real earnings from completed purchases on the creator's tracks
        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var tracks = await _tracks.GetByCreatorIdAsync(userId, creatorUuid);
        var allPurchases = new List<Purchase>();
        foreach (var track in tracks)
        {
            var tp = await _purchases.GetByTrackIdAsync(track.Id);
            allPurchases.AddRange(tp.Where(p => p.Status == "completed"));
        }

        var grossCents = allPurchases.Sum(p => p.AmountCents);
        var totalGross = grossCents / 100m;
        // Use per-purchase floor to match wallet credit calculation in CheckoutService,
        // so the displayed earnings always equal the withdrawable wallet balance.
        var totalEarnedCents = allPurchases.Sum(p => (long)Math.Floor(p.AmountCents * (1 - platformFeeRate)));
        var totalEarned = totalEarnedCents / 100m;
        var totalPlatformFee = Math.Round(totalGross - totalEarned, 2);

        var payouts = await _payouts.GetByCreatorIdAsync(userId);
        var paidOut = payouts.Where(p => p.Status == "completed").Sum(p => p.AmountCents) / 100m;
        var pendingPayouts = payouts.Where(p => p.Status == "pending").Sum(p => p.AmountCents) / 100m;
        var available = totalEarned - paidOut - pendingPayouts;

        return new
        {
            available = Math.Max(0, available),
            pending = pendingPayouts,
            totalEarned,
            totalGross,
            totalPlatformFee,
            platformFeePercent = platformFeeRate,
            totalWithdrawn = paidOut
        };
    }

    public async Task<PayoutResponse> RequestAsync(PayoutRequest request, string creatorId)
    {
        if (request.Amount <= 0)
            throw new ArgumentException("Amount must be greater than zero.");

        if (string.IsNullOrWhiteSpace(creatorId))
            throw new ArgumentException("Creator ID is required for payout requests.");

        // M1: Minimum payout threshold — Stripe fees make micro-payouts uneconomical
        var requestCents = (long)Math.Round(request.Amount * 100, MidpointRounding.AwayFromZero);
        const long MinPayoutCents = 500; // $5.00
        if (requestCents < MinPayoutCents)
            throw new InvalidOperationException($"Minimum payout amount is ${MinPayoutCents / 100m:F2}.");

        // Payout.AmountCents is int (~$21.4M ceiling). Refuse to silently truncate —
        // an oversized request is almost certainly user error or a unit bug.
        if (requestCents > int.MaxValue)
            throw new InvalidOperationException(
                $"Payout amount exceeds the maximum supported per-request size (${int.MaxValue / 100m:F2}). " +
                "Split into multiple smaller payouts.");

        // Verify creator has a connected Stripe account
        var user = await _users.FindByIdAsync(creatorId)
            ?? throw new KeyNotFoundException("Creator not found.");

        if (string.IsNullOrEmpty(user.StripeAccountId))
            throw new InvalidOperationException("You must connect a Stripe account before requesting a payout.");

        // Verify the Connect account has completed onboarding
        var connectStatus = await _gateway.GetConnectAccountStatusAsync(user.StripeAccountId);
        if (!connectStatus.ChargesEnabled || !connectStatus.PayoutsEnabled)
            throw new InvalidOperationException(
                "Your Stripe account onboarding is incomplete. " +
                "Please finish setting up your account before requesting a payout.");

        // ── Atomic balance check + debit + payout creation in one Serializable transaction ──
        // Serializable isolation prevents phantom reads / double-withdrawal races.
        // Both the wallet debit and the payout record commit together — neither can orphan.
        var payout = new Payout
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            AmountCents = (int)requestCents,
            Status = "pending",
            RequestedAt = DateTime.UtcNow
        };

        await using var txHandle = await _transactions.BeginSerializableTransactionAsync();
        try
        {
            var balance = await _wallet.GetBalanceAsync(creatorId);
            if (balance < requestCents)
            {
                await _transactions.RollbackAsync();
                throw new InvalidOperationException("Insufficient balance for this payout.");
            }

            // Both AddTransactionAsync and AddAsync call SaveChangesAsync internally.
            // With an active ITransactionManager transaction those saves flush to the DB
            // but do NOT commit — the commit happens only at CommitAsync() below.
            await _wallet.AddTransactionAsync(new WalletTransaction
            {
                Id = Guid.NewGuid(),
                UserId = creatorId,
                AmountCents = -requestCents,
                Type = "withdrawal",
                Description = $"Payout ${request.Amount:F2}",
                CreatedAt = DateTime.UtcNow
            });

            await _payouts.AddAsync(payout);

            await _transactions.CommitAsync();
            _logger.LogInformation(
                "Payout {PayoutId} debit+record committed atomically: {AmountCents}c for creator {CreatorId}",
                payout.Id, requestCents, creatorId);
        }
        catch (InvalidOperationException)
        {
            // Validation error (insufficient balance) — already rolled back above, re-throw.
            throw;
        }
        catch (Exception ex)
        {
            await _transactions.RollbackAsync();
            _logger.LogError(ex, "Payout transaction failed for creator {CreatorId} — wallet unchanged", creatorId);
            throw new InvalidOperationException("Payout could not be processed. Please try again later.");
        }

        // Initiate Stripe transfer (outside the DB transaction — Stripe calls cannot be transacted)
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

            // Compensate: credit the wallet back since the Stripe transfer failed
            await _wallet.AddTransactionAsync(new WalletTransaction
            {
                Id = Guid.NewGuid(),
                UserId = creatorId,
                AmountCents = requestCents,
                Type = "credit",
                Description = $"Payout refund (transfer failed): ${request.Amount:F2}",
                CreatedAt = DateTime.UtcNow
            });

            payout.Status = "failed";
            payout.FailureReason = ex.Message;
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
            Status = p.Status,
            RequestedAt = p.RequestedAt,
            CompletedAt = p.CompletedAt,
            FailureReason = p.FailureReason
        }).ToList();
    }
}
