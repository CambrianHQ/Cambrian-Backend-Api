namespace Cambrian.Application.Pricing;

/// <summary>
/// Single source of truth for "given a gross purchase amount and a platform fee
/// rate, how many cents does the creator receive?"
///
/// Background: the audit found three independent implementations of this math
/// (CheckoutService, StripeWebhookService, PayoutService). They drifted apart
/// — Floor(Sum(...)) vs Sum(Floor(...)) produced different totals, so the
/// dashboard balance and the withdrawable balance disagreed and payouts were
/// rejected with "insufficient funds" errors. Every wallet credit, refund
/// clawback, and earnings rollup must call this method.
///
/// Invariants this enforces:
///  - Result is never negative (a corrupted feeRate &gt; 1 floors at 0 instead
///    of silently debiting the creator).
///  - Per-purchase floor — sums of credits computed by adding individual
///    ComputeCreatorCents results match the stored WalletTransaction totals.
///  - Pure: no I/O, no logging, no DI. Fully unit-testable.
/// </summary>
public static class CreatorEarningsCalculator
{
    /// <summary>
    /// Returns the creator's share in cents for a single purchase, given the gross
    /// amount and the platform fee rate (0..1, e.g. 0.20 for a 20% fee).
    /// </summary>
    public static long ComputeCreatorCents(long grossCents, decimal feeRate)
    {
        if (grossCents <= 0)
            return 0;

        // (1 - feeRate) can be negative if feeRate is corrupted (>1) or > 1 if
        // feeRate is somehow negative; both are bugs upstream. Floor at 0 in the
        // first case (never silently debit) and at grossCents in the second
        // (creator can never receive more than the buyer paid).
        var multiplier = 1m - feeRate;
        if (multiplier <= 0m)
            return 0;
        if (multiplier > 1m)
            multiplier = 1m;

        var creatorCents = (long)System.Math.Floor((decimal)grossCents * multiplier);
        return System.Math.Max(0L, creatorCents);
    }
}
