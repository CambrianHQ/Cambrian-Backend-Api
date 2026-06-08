using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Identity;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests for BillingService subscription checkout (pre-created Stripe Price IDs),
/// the Customer Portal, and tier validation.
/// </summary>
public sealed class BillingTierTests
{
    private const string CreatorPrice = "price_creator_test";
    private const string ProPrice = "price_pro_test";

    private static (BillingService sut, IPaymentGateway gateway, ISubscriptionRepository subs, UserManager<ApplicationUser> users)
        CreateSut()
    {
        var subs = Substitute.For<ISubscriptionRepository>();
        var gateway = Substitute.For<IPaymentGateway>();
        var subService = Substitute.For<ISubscriptionService>();
        var logger = Substitute.For<ILogger<BillingService>>();
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FrontendUrl"] = "http://localhost:5173",
                ["Stripe:Prices:Creator"] = CreatorPrice,
                ["Stripe:Prices:Pro"] = ProPrice
            })
            .Build();

        var sut = new BillingService(subs, subService, gateway, users, config, logger);
        return (sut, gateway, subs, users);
    }

    // ── Checkout maps tiers to the configured Stripe Price IDs ──

    [Theory]
    [InlineData("creator", CreatorPrice)]
    [InlineData("Creator", CreatorPrice)]
    [InlineData("pro", ProPrice)]
    [InlineData("PRO", ProPrice)]
    public async Task CreateCheckout_UsesConfiguredPriceId_ForTier(string tier, string expectedPriceId)
    {
        var (sut, gateway, _, _) = CreateSut();
        gateway.CreateSubscriptionCheckoutByPriceAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns("https://checkout.stripe.test/session");

        var result = await sut.CreateCheckoutAsync(new BillingCheckoutRequest { Tier = tier }, "user-1", "u@test.com");

        Assert.Equal("https://checkout.stripe.test/session", result.CheckoutUrl);
        await gateway.Received(1).CreateSubscriptionCheckoutByPriceAsync(
            expectedPriceId,
            Arg.Is<string>(r => r.StartsWith("user-1:subscription:")),
            Arg.Any<string>(),
            Arg.Any<string>(),
            "u@test.com");
    }

    [Theory]
    [InlineData("enterprise")]
    [InlineData("premium")]
    [InlineData("free")]
    [InlineData("")]
    [InlineData(null)]
    public async Task CreateCheckout_ThrowsForInvalidTier(string? tier)
    {
        var (sut, _, _, _) = CreateSut();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.CreateCheckoutAsync(new BillingCheckoutRequest { Tier = tier }, "user-1"));
    }

    // ── Customer Portal ──

    [Fact]
    public async Task CreatePortal_UsesSubscriptionCustomerId_WhenPresent()
    {
        var (sut, gateway, subs, _) = CreateSut();
        subs.GetActiveAsync("user-1").Returns(new Subscription { UserId = "user-1", StripeCustomerId = "cus_existing" });
        gateway.CreateBillingPortalSessionAsync("cus_existing", Arg.Any<string>())
            .Returns("https://billing.stripe.test/portal");

        var result = await sut.CreatePortalAsync("user-1", "u@test.com");

        Assert.Equal("https://billing.stripe.test/portal", result.PortalUrl);
        await gateway.DidNotReceive().EnsureCustomerAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task CreatePortal_FindsOrCreatesCustomer_WhenNoSubscriptionCustomer()
    {
        var (sut, gateway, subs, _) = CreateSut();
        subs.GetActiveAsync("user-1").Returns((Subscription?)null);
        gateway.EnsureCustomerAsync("u@test.com").Returns("cus_new");
        gateway.CreateBillingPortalSessionAsync("cus_new", Arg.Any<string>())
            .Returns("https://billing.stripe.test/portal-new");

        var result = await sut.CreatePortalAsync("user-1", "u@test.com");

        Assert.Equal("https://billing.stripe.test/portal-new", result.PortalUrl);
        await gateway.Received(1).EnsureCustomerAsync("u@test.com");
    }

    // ── Status ──

    [Fact]
    public async Task GetStatus_ReturnsFree_WhenNoSubscription()
    {
        var (sut, _, subs, users) = CreateSut();
        subs.GetActiveAsync("user-1").Returns((Subscription?)null);
        users.FindByIdAsync("user-1").Returns((ApplicationUser?)null);

        var result = await sut.GetStatusAsync("user-1");

        Assert.Equal("free", result.Tier);
        Assert.Equal("active", result.Status);
    }
}
