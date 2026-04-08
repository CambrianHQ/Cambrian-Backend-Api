using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Identity;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class BillingTierTests
{
    private static (int amountCents, string planName) MapTier(string? tier)
    {
        var normalized = tier?.ToLowerInvariant() ?? "";
        return normalized switch
        {
            "paid" => (999, "Buyer Subscription"),
            "creator" or "pro" => (999, "Pro Creator"),
            _ => (0, "")
        };
    }

    [Theory]
    [InlineData("paid", 999, "Buyer Subscription")]
    [InlineData("Paid", 999, "Buyer Subscription")]
    [InlineData("PAID", 999, "Buyer Subscription")]
    [InlineData("creator", 999, "Pro Creator")]
    [InlineData("Creator", 999, "Pro Creator")]
    [InlineData("CREATOR", 999, "Pro Creator")]
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

        var subService = Substitute.For<ISubscriptionService>();
        var logger = Substitute.For<ILogger<BillingService>>();
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        var sut = new BillingService(subscriptions, subService, gateway, users, config, logger);

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

        var subService = Substitute.For<ISubscriptionService>();
        var logger = Substitute.For<ILogger<BillingService>>();
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        var sut = new BillingService(subscriptions, subService, gateway, users, config, logger);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.CreateCheckoutAsync(new BillingCheckoutRequest { Tier = "enterprise" }, "user-1"));
    }
}
