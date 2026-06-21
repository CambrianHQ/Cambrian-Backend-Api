using Cambrian.Application.DTOs.Payouts;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Pricing;
using Cambrian.Domain.Constants;
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
            allPurchases.AddRange(tp.Where(p => p.Status == PurchaseStatuses.Completed));
        }

        var grossCents = allPurchases.Sum(p => p.AmountCents);
        var totalGross = grossCents / 100m;
        // Single source of truth: CreatorEarningsCalculator. Per-purchase floor matches
        // the wallet credit math used at sale time, so available == withdrawable.
        var totalEarnedCents = allPurchases.Sum(p =>
            CreatorEarningsCalculator.ComputeCreatorCents(p.AmountCents, platformFeeRate));
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

        // ── Atomic balance check + debit + resumable payout creation ──
        // An existing pending payout is resumed instead of debiting the wallet again.
        // The Stripe idempotency key is derived from the durable payout ID, so a crash
        // after Stripe accepted the transfer but before the response was persisted is safe:
        // the next same-amount request receives the same Stripe transfer.
        Payout payout;
        var created = false;
        var committed = false;
        await using var txHandle = await _transactions.BeginSerializableTransactionAsync();
        try
        {
            var outstanding = await _payouts.GetOutstandingAsync(creatorId);
            if (outstanding is not null)
            {
                if (outstanding.AmountCents != requestCents)
                    throw new InvalidOperationException(
                        "A payout is already processing. Retry that payout before requesting a different amount.");

                payout = outstanding;
                payout.StripeIdempotencyKey ??= $"cambrian-payout-{payout.Id:N}";
                await _payouts.UpdateAsync(payout);
                _logger.LogWarning(
                    "Resuming pending payout {PayoutId} for creator {CreatorId}; wallet will not be debited again",
                    payout.Id, creatorId);
            }
            else
            {
                var balance = await _wallet.GetBalanceAsync(creatorId);
                if (balance < requestCents)
                    throw new InvalidOperationException("Insufficient balance for this payout.");

                payout = new Payout
                {
                    Id = Guid.NewGuid(),
                    CreatorId = creatorId,
                    AmountCents = (int)requestCents,
                    Status = "pending",
                    RequestedAt = DateTime.UtcNow,
                };
                payout.StripeIdempotencyKey = $"cambrian-payout-{payout.Id:N}";

                // Both writes flush inside the active transaction; only CommitAsync
                // makes the debit and payout row durable.
                await _wallet.AddTransactionAsync(new WalletTransaction
                {
                    Id = Guid.NewGuid(),
                    UserId = creatorId,
                    AmountCents = -requestCents,
                    Type = "withdrawal",
                    Description = $"Payout {payout.Id}: ${request.Amount:F2}",
                    CreatedAt = DateTime.UtcNow
                });
                await _payouts.AddAsync(payout);
                created = true;
            }

            await _transactions.CommitAsync();
            committed = true;

            if (created)
            {
                Observability.CambrianMetrics.PayoutCreated.Add(1);
                _logger.LogInformation(
                    "Payout {PayoutId} debit+record committed atomically: {AmountCents}c for creator {CreatorId}",
                    payout.Id, requestCents, creatorId);
            }
        }
        catch (InvalidOperationException)
        {
            if (!committed)
                await _transactions.RollbackAsync();
            throw;
        }
        catch (Exception ex)
        {
            if (!committed)
                await _transactions.RollbackAsync();
            _logger.LogError(ex, "Payout transaction failed for creator {CreatorId} — wallet unchanged", creatorId);
            throw new InvalidOperationException("Payout could not be processed. Please try again later.");
        }

        // Stripe is outside the DB transaction. Provider idempotency plus the durable
        // pending row makes the operation resumable across timeouts and process crashes.
        try
        {
            var transferId = await _gateway.CreateTransferAsync(
                user.StripeAccountId,
                payout.AmountCents,
                $"Cambrian payout {payout.Id}",
                payout.StripeIdempotencyKey!);

            payout.StripeTransferId = transferId;
            payout.Status = "completed";
            payout.FailureReason = null;
            payout.CompletedAt = DateTime.UtcNow;
            await _payouts.UpdateAsync(payout);
            Observability.CambrianMetrics.PayoutApproved.Add(1);

            _logger.LogInformation(
                "Payout {PayoutId} completed: {AmountCents}c → {AccountId} (transfer {TransferId})",
                payout.Id, requestCents, user.StripeAccountId, transferId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Payout {PayoutId} Stripe transfer result is unconfirmed; retaining pending row for idempotent retry",
                payout.Id);

            // Do not refund here: a transport error can occur after Stripe accepted the
            // transfer. Refunding would let the creator keep both the transfer and wallet
            // balance. Retain the pending row and retry with the same provider key.
            payout.Status = "pending";
            payout.FailureReason = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            try
            {
                await _payouts.UpdateAsync(payout);
            }
            catch (Exception persistEx)
            {
                _logger.LogCritical(
                    persistEx,
                    "Failed to persist pending status for payout {PayoutId}; manual reconciliation required",
                    payout.Id);
            }

            throw new PayoutPendingException();
        }

        return new PayoutResponse
        {
            Id = payout.Id,
            Amount = payout.AmountCents / 100m,
            Status = payout.Status,
            RequestedAt = payout.RequestedAt,
            CompletedAt = payout.CompletedAt,
        };
    }

    public async Task<IReadOnlyCollection<PayoutResponse>> GetHistoryAsync(string userId, int take = 50)
    {
        var payouts = await _payouts.GetByCreatorIdAsync(userId);

        return payouts.Take(take).Select(p => new PayoutResponse
        {
            Id = p.Id,
            Amount = p.AmountCents / 100m,
            Status = p.Status,
            RequestedAt = p.RequestedAt,
            CompletedAt = p.CompletedAt,
            FailureReason = p.FailureReason
        }).ToList();
    }
}
