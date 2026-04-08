using System.Security.Claims;
using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.DTOs.Subscriptions;
using Cambrian.Application.DTOs.Wallet;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Phase 2 tests: payment refactor — service delegation, Purchase.AmountCents, creator wallet credit.
/// </summary>
public sealed class Phase2PaymentTests
{
    // ── BillingService Tests ──

    [Fact]
    public async Task BillingService_CreateCheckout_ThrowsOnInvalidTier()
    {
        var subs = Substitute.For<ISubscriptionRepository>();
        var gateway = Substitute.For<IPaymentGateway>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:FrontendUrl"] = "http://localhost:5173" })
            .Build();
        var subService = Substitute.For<ISubscriptionService>();
        var logger = Substitute.For<ILogger<BillingService>>();
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        var sut = new BillingService(subs, subService, gateway, users, config, logger);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.CreateCheckoutAsync(new BillingCheckoutRequest { Tier = "invalid" }, "user-1"));
    }

    [Theory]
    [InlineData("paid", 499)]
    [InlineData("pro", 999)]
    public async Task BillingService_CreateCheckout_UsesCorrectAmount(string tier, int expectedCents)
    {
        var subs = Substitute.For<ISubscriptionRepository>();
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.CreateSubscriptionCheckoutAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://checkout.stripe.com/test");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:FrontendUrl"] = "http://localhost:5173" })
            .Build();
        var subService = Substitute.For<ISubscriptionService>();
        var logger = Substitute.For<ILogger<BillingService>>();
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        var sut = new BillingService(subs, subService, gateway, users, config, logger);

        var result = await sut.CreateCheckoutAsync(new BillingCheckoutRequest { Tier = tier }, "user-1");

        Assert.Equal("https://checkout.stripe.com/test", result.CheckoutUrl);
        await gateway.Received(1).CreateSubscriptionCheckoutAsync(
            expectedCents, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // ── SubscriptionService Tests ──

    [Fact]
    public async Task SubscriptionService_Cancel_ThrowsWhenNoActiveSubscription()
    {
        var subs = Substitute.For<ISubscriptionRepository>();
        subs.GetActiveAsync("user-1").Returns((Subscription?)null);
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        var sut = new SubscriptionService(subs, Substitute.For<ITransactionManager>(), users);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CancelAsync("user-1"));
    }

    [Fact]
    public async Task SubscriptionService_Update_CreatesNewSubscription()
    {
        var subs = Substitute.For<ISubscriptionRepository>();
        subs.GetActiveAsync("user-1").Returns((Subscription?)null);
        subs.CreateAsync(Arg.Any<Subscription>()).Returns(ci => ci.Arg<Subscription>());
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        users.FindByIdAsync("user-1").Returns(new ApplicationUser { Id = "user-1", Tier = "free" });
        users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);
        var sut = new SubscriptionService(subs, Substitute.For<ITransactionManager>(), users);

        var result = await sut.UpdateAsync(new UpdateSubscriptionRequest { Plan = "pro" }, "user-1");

        Assert.Equal("pro", result.Plan);
        Assert.Equal("active", result.Status);
    }

    // ── WalletService Tests ──

    [Fact]
    public async Task WalletService_WithdrawAsync_ThrowsOnInsufficientBalance()
    {
        var repo = Substitute.For<IWalletRepository>();
        repo.GetBalanceAsync("user-1").Returns(1000L); // $10.00
        var sut = new WalletService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.WithdrawAsync(50.00m, "user-1")); // $50 > $10
    }

    [Fact]
    public async Task WalletService_WithdrawAsync_ThrowsOnNegativeAmount()
    {
        var repo = Substitute.For<IWalletRepository>();
        var sut = new WalletService(repo);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.WithdrawAsync(-10.00m, "user-1"));
    }

    [Fact]
    public async Task WalletService_WithdrawAsync_CreatesNegativeTransaction()
    {
        var repo = Substitute.For<IWalletRepository>();
        repo.AtomicWithdrawAsync("user-1", 2500, Arg.Any<string>()).Returns(true);
        var sut = new WalletService(repo);

        await sut.WithdrawAsync(25.00m, "user-1");

        await repo.Received(1).AtomicWithdrawAsync("user-1", 2500,
            Arg.Is<string>(d => d.Contains("25.00")));
    }

    // ── Purchase.AmountCents Tests ──

    [Fact]
    public async Task PurchaseService_StoresAmountInCents()
    {
        var purchases = Substitute.For<IPurchaseRepository>();
        var tracks = Substitute.For<ITrackRepository>();
        var library = Substitute.For<ILibraryRepository>();
        var invoices = Substitute.For<IInvoiceRepository>();
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.GetCheckoutSessionAsync(Arg.Any<string>())
            .Returns(new CheckoutSessionInfo { SessionId = "sess_test", Status = "paid" });

        var trackId = Guid.NewGuid();
        tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId,
            Title = "Test Beat",
            Price = 29.99m,
            NonExclusivePriceCents = 2999,
            CreatorId = "creator-1"
        });
        purchases.GetByBuyerIdAsync("buyer-1").Returns(new List<Purchase>());

        var sut = new PurchaseService(purchases, tracks, library, invoices,
            Substitute.For<ILicenseService>(), gateway, Substitute.For<ITransactionManager>(), Substitute.For<Microsoft.Extensions.Logging.ILogger<PurchaseService>>());
        var result = await sut.CreateAsync(
            new Application.DTOs.Purchases.PurchaseCreateRequest { TrackId = trackId.ToString(), LicenseType = "non-exclusive", StripeSessionId = "sess_test" },
            "buyer-1");

        Assert.Equal(2999, result.AmountCents);
        await purchases.Received(1).AddAsync(Arg.Is<Purchase>(p => p.AmountCents == 2999));
    }

    [Fact]
    public async Task PurchaseService_UsesExclusivePrice_WhenLicenseTypeIsExclusive()
    {
        var purchases = Substitute.For<IPurchaseRepository>();
        var tracks = Substitute.For<ITrackRepository>();
        var library = Substitute.For<ILibraryRepository>();
        var invoices = Substitute.For<IInvoiceRepository>();
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.GetCheckoutSessionAsync(Arg.Any<string>())
            .Returns(new CheckoutSessionInfo { SessionId = "sess_test", Status = "paid" });

        var trackId = Guid.NewGuid();
        tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId,
            Title = "Exclusive Beat",
            Price = 10.00m,
            NonExclusivePriceCents = 1000,
            ExclusivePriceCents = 5000,
            CreatorId = "creator-1"
        });
        purchases.GetByBuyerIdAsync("buyer-1").Returns(new List<Purchase>());
        tracks.TryMarkExclusiveSoldAsync(trackId).Returns(true);

        var sut = new PurchaseService(purchases, tracks, library, invoices,
            Substitute.For<ILicenseService>(), gateway, Substitute.For<ITransactionManager>(), Substitute.For<Microsoft.Extensions.Logging.ILogger<PurchaseService>>());
        var result = await sut.CreateAsync(
            new Application.DTOs.Purchases.PurchaseCreateRequest { TrackId = trackId.ToString(), LicenseType = "exclusive", StripeSessionId = "sess_test" },
            "buyer-1");

        Assert.Equal(5000, result.AmountCents);
    }
}
