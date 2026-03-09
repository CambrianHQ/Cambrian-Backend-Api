using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Configuration;
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
    public async Task BillingService_GetStatus_ReturnsFreeWhenNoSubscription()
    {
        var subscriptions = Substitute.For<ISubscriptionRepository>();
        subscriptions.GetActiveAsync("user-1").Returns((Subscription?)null);
        var gateway = Substitute.For<IPaymentGateway>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FrontendUrl"] = "http://localhost:5173"
            })
            .Build();

        var sut = new BillingService(subscriptions, gateway, config);

        var result = await sut.GetStatusAsync("user-1");

        Assert.Equal("free", result.Tier);
        Assert.Equal("active", result.Status);
    }

    [Fact]
    public async Task BillingService_CreateCheckout_ThrowsForInvalidTier()
    {
        var subscriptions = Substitute.For<ISubscriptionRepository>();
        var gateway = Substitute.For<IPaymentGateway>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FrontendUrl"] = "http://localhost:5173"
            })
            .Build();

        var sut = new BillingService(subscriptions, gateway, config);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.CreateCheckoutAsync(new BillingCheckoutRequest { Tier = "enterprise" }, "user-1"));
    }
}
