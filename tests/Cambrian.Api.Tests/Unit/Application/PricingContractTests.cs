using System.Collections.Generic;
using Cambrian.Application.Configuration;
using Cambrian.Application.Pricing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Cambrian.Api.Tests.Unit.Application;

/// <summary>
/// Pins the canonical pricing contract and proves the PricingContract facade
/// delegates to the underlying sources (never holds drifting copies of the numbers).
/// </summary>
public sealed class PricingContractTests
{
    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Subscriptions_MatchCanonicalCents()
    {
        PricingContract.CreatorSubscriptionCents.Should().Be(599);  // $5.99/mo
        PricingContract.ProSubscriptionCents.Should().Be(1299);    // $12.99/mo
    }

    [Fact]
    public void CreditPacks_MatchCanonicalCentsAndCredits()
    {
        PricingContract.SingleCreditPack.Credits.Should().Be(1);
        PricingContract.SingleCreditPack.PriceCents.Should().Be(900);  // $9
        PricingContract.TripleCreditPack.Credits.Should().Be(3);
        PricingContract.TripleCreditPack.PriceCents.Should().Be(2400); // $24
        PricingContract.TenCreditPack.Credits.Should().Be(10);
        PricingContract.TenCreditPack.PriceCents.Should().Be(6900);    // $69
    }

    [Fact]
    public void MonthlyReleaseReadyGrants_MatchCanonical()
    {
        PricingContract.CreatorMonthlyReleaseReadyCredits.Should().Be(3);
        PricingContract.ProMonthlyReleaseReadyCredits.Should().Be(10);
    }

    [Fact]
    public void AuthorshipRecord_DefaultsTo1000()
    {
        PricingContract.AuthorshipRecordDefaultCents.Should().Be(1000); // $10
        PricingContract.ResolveAuthorshipRecordCents(Config(new())).Should().Be(1000);
    }

    [Fact]
    public void AuthorshipRecord_HonorsConfigOverride()
    {
        var config = Config(new() { ["AuthorshipRecord:PriceCents"] = "3499" });
        PricingContract.ResolveAuthorshipRecordCents(config).Should().Be(3499);
    }

    [Fact]
    public void Facade_DelegatesToUnderlyingSources_NoCopies()
    {
        PricingContract.CreatorSubscriptionCents.Should().Be(TierManifest.Creator.PriceCents);
        PricingContract.ProSubscriptionCents.Should().Be(TierManifest.Pro.PriceCents);
        PricingContract.CreatorMonthlyReleaseReadyCredits.Should().Be(TierManifest.Creator.ReleaseReadyCreditsPerMonth);
        PricingContract.ProMonthlyReleaseReadyCredits.Should().Be(TierManifest.Pro.ReleaseReadyCreditsPerMonth);
        PricingContract.SingleCreditPack.Should().BeSameAs(CreditPackCatalog.Find("single"));
        PricingContract.TripleCreditPack.Should().BeSameAs(CreditPackCatalog.Find("triple"));
        PricingContract.TenCreditPack.Should().BeSameAs(CreditPackCatalog.Find("ten"));
    }

    [Fact]
    public void CreditPackForKey_ResolvesStableKeys()
    {
        PricingContract.CreditPackForKey(PricingContract.Keys.CreditSingle)!.PriceCents.Should().Be(900);
        PricingContract.CreditPackForKey(PricingContract.Keys.CreditTriple)!.PriceCents.Should().Be(2400);
        PricingContract.CreditPackForKey(PricingContract.Keys.CreditTen)!.PriceCents.Should().Be(6900);
        PricingContract.CreditPackForKey("nope").Should().BeNull();
    }

    [Fact]
    public void ResolveSubscriptionPriceId_ReadsBillingConfigKeys()
    {
        var config = Config(new()
        {
            ["Stripe:Prices:Creator"] = "price_creator_123",
            ["Stripe:Prices:Pro"] = "price_pro_456",
        });

        PricingContract.ResolveSubscriptionPriceId(PricingContract.Keys.CreatorSubscription, config)
            .Should().Be("price_creator_123");
        PricingContract.ResolveSubscriptionPriceId(PricingContract.Keys.ProSubscription, config)
            .Should().Be("price_pro_456");
        PricingContract.ResolveSubscriptionPriceId("sub.unknown", config).Should().BeNull();
    }
}
