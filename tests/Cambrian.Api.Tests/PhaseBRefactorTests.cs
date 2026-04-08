using Cambrian.Application.Pricing;
using Cambrian.Domain.Constants;

namespace Cambrian.Api.Tests;

/// <summary>
/// Phase B (refactor) tests:
///   - PurchaseStatuses constants are the canonical strings (must match on-disk values)
///   - CreatorEarningsCalculator is the single source of truth for sale → wallet credit math
/// </summary>
[Trait("Category", "Critical")]
public sealed class PhaseBRefactorTests
{
    // ── PurchaseStatuses ─────────────────────────────────────────────

    [Fact]
    public void PurchaseStatuses_LiteralsMatchOnDiskValues()
    {
        // These exact strings are persisted in Purchase.Status. Changing them is a
        // schema break that requires a data migration. Pinning them in a test makes
        // accidental drift fail loudly at compile-test rather than at runtime.
        Assert.Equal("pending", PurchaseStatuses.Pending);
        Assert.Equal("completed", PurchaseStatuses.Completed);
        Assert.Equal("refunded", PurchaseStatuses.Refunded);
        Assert.Equal("disputed", PurchaseStatuses.Disputed);
    }

    // ── CreatorEarningsCalculator ────────────────────────────────────

    [Fact]
    public void Calculator_NormalCase_TwentyPercentFee()
    {
        Assert.Equal(800L, CreatorEarningsCalculator.ComputeCreatorCents(1000, 0.20m));
    }

    [Fact]
    public void Calculator_ZeroFee_PaysFullAmount()
    {
        Assert.Equal(1000L, CreatorEarningsCalculator.ComputeCreatorCents(1000, 0m));
    }

    [Fact]
    public void Calculator_HundredPercentFee_PaysZero()
    {
        Assert.Equal(0L, CreatorEarningsCalculator.ComputeCreatorCents(1000, 1m));
    }

    [Fact]
    public void Calculator_FloorsRoundsDown()
    {
        // 1000 * (1 - 0.123) = 877.0 → floor → 877
        Assert.Equal(877L, CreatorEarningsCalculator.ComputeCreatorCents(1000, 0.123m));
        // 999 * (1 - 0.20) = 799.2 → floor → 799 (proves we floor not round)
        Assert.Equal(799L, CreatorEarningsCalculator.ComputeCreatorCents(999, 0.20m));
    }

    [Fact]
    public void Calculator_CorruptedFeeRateOverOne_FloorsAtZero()
    {
        // A corrupted TierManifest with a >1 feeRate must NEVER produce a negative
        // credit (which would silently debit the creator).
        Assert.Equal(0L, CreatorEarningsCalculator.ComputeCreatorCents(1000, 1.5m));
        Assert.Equal(0L, CreatorEarningsCalculator.ComputeCreatorCents(1000, 2m));
    }

    [Fact]
    public void Calculator_CorruptedNegativeFeeRate_CapsAtGross()
    {
        // A negative feeRate would otherwise pay the creator more than gross.
        Assert.Equal(1000L, CreatorEarningsCalculator.ComputeCreatorCents(1000, -0.5m));
    }

    [Fact]
    public void Calculator_ZeroOrNegativeGross_ReturnsZero()
    {
        Assert.Equal(0L, CreatorEarningsCalculator.ComputeCreatorCents(0, 0.20m));
        Assert.Equal(0L, CreatorEarningsCalculator.ComputeCreatorCents(-1, 0.20m));
    }

    [Fact]
    public void Calculator_PerPurchaseFloorMatchesSumOfFloors()
    {
        // Audit fix #5: previously CreatorService computed Floor(Sum(...)*rate)
        // while CheckoutService used per-purchase Floor — they disagreed by a few
        // cents. Asserting Sum(Floor(...)) is the canonical sum.
        decimal feeRate = 0.20m;
        long[] grossAmounts = { 999, 999, 999 }; // 3 sales, each 999c

        var perPurchaseSum =
            CreatorEarningsCalculator.ComputeCreatorCents(grossAmounts[0], feeRate) +
            CreatorEarningsCalculator.ComputeCreatorCents(grossAmounts[1], feeRate) +
            CreatorEarningsCalculator.ComputeCreatorCents(grossAmounts[2], feeRate);

        // 999 * 0.8 = 799.2 → floor 799 → 3*799 = 2397
        Assert.Equal(2397L, perPurchaseSum);
    }
}
