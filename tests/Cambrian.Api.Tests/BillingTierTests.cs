using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class BillingTierTests
{
    private static (int amountCents, string planName) MapTier(string? tier)
    {
        var normalized = tier?.ToLowerInvariant() ?? "";
        return normalized switch
        {
            "paid" => (499, "Paid Listener"),
            "creator" => (999, "Creator"),
            _ => (0, "")
        };
    }

    [Theory]
    [InlineData("paid", 499, "Paid Listener")]
    [InlineData("Paid", 499, "Paid Listener")]
    [InlineData("PAID", 499, "Paid Listener")]
    [InlineData("creator", 999, "Creator")]
    [InlineData("Creator", 999, "Creator")]
    [InlineData("CREATOR", 999, "Creator")]
    public void ValidTiers_MapToCorrectPricing(string tier, int expectedCents, string expectedName)
    {
        var (cents, name) = MapTier(tier);
        Assert.Equal(expectedCents, cents);
        Assert.Equal(expectedName, name);
    }

    [Theory]
    [InlineData("free")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("enterprise")]
    [InlineData("premium")]
    public void InvalidTiers_MapToZero(string? tier)
    {
        var (cents, name) = MapTier(tier);
        Assert.Equal(0, cents);
        Assert.Equal("", name);
    }

    [Fact]
    public async Task BillingService_CreateCheckout_ThrowsForInvalidTier()
    {
        var subscriptions = Substitute.For<ISubscriptionRepository>();
        var gateway = Substitute.For<IPaymentGateway>();
        var sut = new BillingService(subscriptions, gateway);

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.CreateCheckoutAsync("enterprise", "user-1", "http://localhost"));
    }

    [Fact]
    public async Task BillingService_CreateCheckout_CallsGatewayForValidTier()
    {
        var subscriptions = Substitute.For<ISubscriptionRepository>();
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.CreateSubscriptionCheckoutAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://checkout.stripe.com/session123");

        var sut = new BillingService(subscriptions, gateway);

        var result = await sut.CreateCheckoutAsync("paid", "user-1", "http://localhost:5173");

        Assert.Equal("https://checkout.stripe.com/session123", result.CheckoutUrl);
        await gateway.Received(1).CreateSubscriptionCheckoutAsync(
            499, "Paid Listener",
            "user-1:subscription:paid",
            Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task BillingService_GetStatus_ReturnsFreeWhenNoSubscription()
    {
        var subscriptions = Substitute.For<ISubscriptionRepository>();
        var gateway = Substitute.For<IPaymentGateway>();
        subscriptions.GetActiveAsync("user-1").Returns((Domain.Entities.Subscription?)null);

        var sut = new BillingService(subscriptions, gateway);

        var result = await sut.GetStatusAsync("user-1");

        Assert.Equal("free", result.Tier);
        Assert.Equal("active", result.Status);
    }
}
