using Cambrian.Application.DTOs.Payouts;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

public sealed class PayoutServiceTests
{
    private readonly IPayoutRepository _payouts = Substitute.For<IPayoutRepository>();
    private readonly IPurchaseRepository _purchases = Substitute.For<IPurchaseRepository>();
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly UserManager<ApplicationUser> _users;
    private readonly IWalletRepository _wallet = Substitute.For<IWalletRepository>();
    private readonly PayoutService _sut;

    public PayoutServiceTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        _users = Substitute.For<UserManager<ApplicationUser>>(
            store, null, null, null, null, null, null, null, null);

        _sut = new PayoutService(
            _payouts, _purchases, _tracks, _gateway, _users, _wallet,
            Substitute.For<ILogger<PayoutService>>());
    }

    // ── Input validation ──

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task RequestAsync_Throws_WhenAmountNotPositive(decimal amount)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.RequestAsync(new PayoutRequest { Amount = amount }, "creator-1"));
    }

    [Fact]
    public async Task RequestAsync_Throws_WhenCreatorIdEmpty()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.RequestAsync(new PayoutRequest { Amount = 10m }, ""));
    }

    // ── No minimum payout amount ──

    [Fact]
    public async Task RequestAsync_AcceptsAnyPositiveAmount_NoMinimum()
    {
        var user = new ApplicationUser { Id = "c1", StripeAccountId = "acct_1" };
        _users.FindByIdAsync("c1").Returns(user);
        _wallet.GetBalanceAsync("c1").Returns(100L); // 100 cents = $1.00
        _gateway.CreateTransferAsync("acct_1", 1L, Arg.Any<string>())
            .Returns("tr_1");

        // $0.01 — the smallest possible amount
        var result = await _sut.RequestAsync(new PayoutRequest { Amount = 0.01m }, "c1");

        Assert.Equal("completed", result.Status);
        Assert.Equal(0.01m, result.Amount);
    }

    // ── Stripe account required ──

    [Fact]
    public async Task RequestAsync_Throws_WhenNoStripeAccount()
    {
        var user = new ApplicationUser { Id = "c1", StripeAccountId = null };
        _users.FindByIdAsync("c1").Returns(user);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RequestAsync(new PayoutRequest { Amount = 50m }, "c1"));

        Assert.Contains("connect a Stripe account", ex.Message);
    }

    // ── Balance validation ──

    [Fact]
    public async Task RequestAsync_Throws_WhenInsufficientBalance()
    {
        var user = new ApplicationUser { Id = "c1", StripeAccountId = "acct_1" };
        _users.FindByIdAsync("c1").Returns(user);
        _wallet.GetBalanceAsync("c1").Returns(1000L); // $10.00

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RequestAsync(new PayoutRequest { Amount = 50m }, "c1")); // $50

        Assert.Contains("Insufficient balance", ex.Message);
    }

    // ── Successful payout ──

    [Fact]
    public async Task RequestAsync_TransfersViaStripe_AndDebitsWallet()
    {
        var user = new ApplicationUser { Id = "c1", StripeAccountId = "acct_1" };
        _users.FindByIdAsync("c1").Returns(user);
        _wallet.GetBalanceAsync("c1").Returns(10000L); // $100
        _gateway.CreateTransferAsync("acct_1", 5000L, Arg.Any<string>())
            .Returns("tr_123");

        var result = await _sut.RequestAsync(new PayoutRequest { Amount = 50m }, "c1");

        Assert.Equal("completed", result.Status);
        Assert.Equal(50m, result.Amount);

        // Verify wallet was debited
        await _wallet.Received(1).AddTransactionAsync(
            Arg.Is<WalletTransaction>(t => t.AmountCents == -5000 && t.Type == "withdrawal"));

        // Verify Stripe transfer was initiated
        await _gateway.Received(1).CreateTransferAsync("acct_1", 5000L, Arg.Any<string>());
    }

    // ── Failed transfer refunds wallet ──

    [Fact]
    public async Task RequestAsync_RefundsWallet_WhenStripeTransferFails()
    {
        var user = new ApplicationUser { Id = "c1", StripeAccountId = "acct_1" };
        _users.FindByIdAsync("c1").Returns(user);
        _wallet.GetBalanceAsync("c1").Returns(10000L);
        _gateway.CreateTransferAsync("acct_1", Arg.Any<long>(), Arg.Any<string>())
            .Throws(new Exception("Stripe transfer failed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RequestAsync(new PayoutRequest { Amount = 50m }, "c1"));

        Assert.Contains("transfer failed", ex.Message);

        // Wallet should have been debited then refunded (2 transactions)
        await _wallet.Received(2).AddTransactionAsync(Arg.Any<WalletTransaction>());

        // Payout record should be marked failed
        await _payouts.Received(1).UpdateAsync(
            Arg.Is<Payout>(p => p.Status == "failed"));
    }

    // ── Earnings ──

    [Fact]
    public async Task GetEarningsAsync_ComputesFromPurchasesAndPayouts()
    {
        _tracks.GetByCreatorIdAsync("c1").Returns(new List<Track>
        {
            new() { Id = Guid.NewGuid(), CreatorId = "c1" }
        });
        var trackId = (await _tracks.GetByCreatorIdAsync("c1")).First().Id;
        _purchases.GetByTrackIdAsync(trackId).Returns(new List<Purchase>
        {
            new() { AmountCents = 5000, Status = "completed" },
            new() { AmountCents = 3000, Status = "completed" }
        });
        _payouts.GetByCreatorIdAsync("c1").Returns(new List<Payout>
        {
            new() { AmountCents = 2000, Status = "completed" }
        });

        var result = await _sut.GetEarningsAsync("c1");

        Assert.NotNull(result);
    }

    // ── History ──

    [Fact]
    public async Task GetHistoryAsync_ReturnsFormattedPayouts()
    {
        _payouts.GetByCreatorIdAsync("c1").Returns(new List<Payout>
        {
            new() { AmountCents = 5000, Status = "completed" },
            new() { AmountCents = 3000, Status = "pending" }
        });

        var history = await _sut.GetHistoryAsync("c1");

        Assert.Equal(2, history.Count);
        Assert.Equal(50m, history.First().Amount);
    }
}
