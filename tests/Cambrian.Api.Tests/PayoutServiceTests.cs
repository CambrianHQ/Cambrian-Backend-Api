using Cambrian.Application.DTOs.Payouts;
using Cambrian.Application.Exceptions;
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
    private readonly ITransactionManager _transactions = Substitute.For<ITransactionManager>();
    private readonly ICreatorIdentityRepository _creators = Substitute.For<ICreatorIdentityRepository>();
    private readonly PayoutService _sut;

    public PayoutServiceTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        _users = Substitute.For<UserManager<ApplicationUser>>(
            store, null, null, null, null, null, null, null, null);

        // ITransactionManager returns a no-op disposable from BeginSerializableTransactionAsync
        var txHandle = Substitute.For<IAsyncDisposable>();
        _transactions.BeginSerializableTransactionAsync().Returns(txHandle);

        _sut = new PayoutService(
            _payouts, _purchases, _tracks, _gateway, _users, _wallet, _transactions,
            _creators,
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

    // ── Minimum payout threshold ──

    [Fact]
    public async Task RequestAsync_RejectsBelowMinimumPayout()
    {
        var user = new ApplicationUser { Id = "c1", StripeAccountId = "acct_1" };
        _users.FindByIdAsync("c1").Returns(user);
        _gateway.GetConnectAccountStatusAsync("acct_1").Returns(new ConnectAccountStatus
            { AccountId = "acct_1", Status = "active", ChargesEnabled = true, PayoutsEnabled = true });

        // $4.99 — below $5.00 minimum
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RequestAsync(new PayoutRequest { Amount = 4.99m }, "c1"));

        Assert.Contains("5.00", ex.Message);
    }

    [Fact]
    public async Task RequestAsync_AcceptsMinimumPayout()
    {
        var user = new ApplicationUser { Id = "c1", StripeAccountId = "acct_1" };
        _users.FindByIdAsync("c1").Returns(user);
        _gateway.GetConnectAccountStatusAsync("acct_1").Returns(new ConnectAccountStatus
            { AccountId = "acct_1", Status = "active", ChargesEnabled = true, PayoutsEnabled = true });
        _wallet.GetBalanceAsync("c1").Returns(500L);
        _gateway.CreateTransferAsync("acct_1", 500L, Arg.Any<string>(), Arg.Any<string>())
            .Returns("tr_1");

        // $5.00 — exactly at minimum
        var result = await _sut.RequestAsync(new PayoutRequest { Amount = 5.00m }, "c1");

        Assert.Equal("completed", result.Status);
        Assert.Equal(5.00m, result.Amount);
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
        _gateway.GetConnectAccountStatusAsync("acct_1").Returns(new ConnectAccountStatus
            { AccountId = "acct_1", Status = "active", ChargesEnabled = true, PayoutsEnabled = true });
        _wallet.GetBalanceAsync("c1").Returns(100L); // only $1.00 available

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RequestAsync(new PayoutRequest { Amount = 50m }, "c1")); // $50

        Assert.Contains("Insufficient balance", ex.Message);
        // Wallet debit must not have been called
        await _wallet.DidNotReceive().AddTransactionAsync(Arg.Any<WalletTransaction>());
    }

    // ── Successful payout ──

    [Fact]
    public async Task RequestAsync_TransfersViaStripe_AndDebitsWallet()
    {
        var user = new ApplicationUser { Id = "c1", StripeAccountId = "acct_1" };
        _users.FindByIdAsync("c1").Returns(user);
        _gateway.GetConnectAccountStatusAsync("acct_1").Returns(new ConnectAccountStatus
            { AccountId = "acct_1", Status = "active", ChargesEnabled = true, PayoutsEnabled = true });
        _wallet.GetBalanceAsync("c1").Returns(10000L); // $100 available
        _gateway.CreateTransferAsync("acct_1", 5000L, Arg.Any<string>(), Arg.Any<string>())
            .Returns("tr_123");

        var result = await _sut.RequestAsync(new PayoutRequest { Amount = 50m }, "c1");

        Assert.Equal("completed", result.Status);
        Assert.Equal(50m, result.Amount);

        // Verify debit transaction was created inside the atomic block
        await _wallet.Received(1).AddTransactionAsync(
            Arg.Is<WalletTransaction>(t => t.AmountCents == -5000L && t.Type == "withdrawal"));

        // Verify Stripe transfer was initiated
        await _gateway.Received(1).CreateTransferAsync(
            "acct_1", 5000L, Arg.Any<string>(), Arg.Is<string>(k => k.StartsWith("cambrian-payout-")));

        // Transaction committed
        await _transactions.Received(1).CommitAsync();
    }

    // ── Atomicity: debit + payout row committed together ──

    [Fact]
    public async Task RequestAsync_CommitsTransactionAtomically_WithDebitAndPayoutRow()
    {
        var user = new ApplicationUser { Id = "c1", StripeAccountId = "acct_1" };
        _users.FindByIdAsync("c1").Returns(user);
        _gateway.GetConnectAccountStatusAsync("acct_1").Returns(new ConnectAccountStatus
            { AccountId = "acct_1", Status = "active", ChargesEnabled = true, PayoutsEnabled = true });
        _wallet.GetBalanceAsync("c1").Returns(10000L);
        _gateway.CreateTransferAsync(
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("tr_ok");

        await _sut.RequestAsync(new PayoutRequest { Amount = 50m }, "c1");

        // All three persistence calls happen, then commit
        await _wallet.Received(1).AddTransactionAsync(Arg.Any<WalletTransaction>());
        await _payouts.Received(1).AddAsync(Arg.Any<Payout>());
        await _transactions.Received(1).CommitAsync();
        await _transactions.DidNotReceive().RollbackAsync();
    }

    // ── Unconfirmed transfer remains resumable ──

    [Fact]
    public async Task RequestAsync_RetainsPendingPayout_WhenStripeTransferResultIsUnconfirmed()
    {
        var user = new ApplicationUser { Id = "c1", StripeAccountId = "acct_1" };
        _users.FindByIdAsync("c1").Returns(user);
        _gateway.GetConnectAccountStatusAsync("acct_1").Returns(new ConnectAccountStatus
            { AccountId = "acct_1", Status = "active", ChargesEnabled = true, PayoutsEnabled = true });
        _wallet.GetBalanceAsync("c1").Returns(10000L);
        _gateway.CreateTransferAsync(
                "acct_1", Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>())
            .Throws(new Exception("Stripe transfer failed"));

        var ex = await Assert.ThrowsAsync<PayoutPendingException>(() =>
            _sut.RequestAsync(new PayoutRequest { Amount = 50m }, "c1"));

        Assert.Contains("still processing", ex.Message);

        // An uncertain provider result must never refund the wallet: Stripe may have
        // accepted the transfer before the connection failed.
        await _wallet.DidNotReceive().AddTransactionAsync(
            Arg.Is<WalletTransaction>(t => t.Type == "credit"));
        await _payouts.Received(1).UpdateAsync(
            Arg.Is<Payout>(p => p.Status == "pending"
                && p.FailureReason == "Stripe transfer failed"
                && p.StripeIdempotencyKey != null));
    }

    [Fact]
    public async Task RequestAsync_ResumesPendingPayout_WithoutSecondWalletDebit()
    {
        var user = new ApplicationUser { Id = "c1", StripeAccountId = "acct_1" };
        _users.FindByIdAsync("c1").Returns(user);
        _gateway.GetConnectAccountStatusAsync("acct_1").Returns(new ConnectAccountStatus
            { AccountId = "acct_1", Status = "active", ChargesEnabled = true, PayoutsEnabled = true });

        var pending = new Payout
        {
            Id = Guid.NewGuid(),
            CreatorId = "c1",
            AmountCents = 5000,
            Status = "pending",
            StripeIdempotencyKey = "cambrian-payout-existing"
        };
        _payouts.GetOutstandingAsync("c1").Returns(pending);
        _gateway.CreateTransferAsync(
                "acct_1", 5000L, Arg.Any<string>(), "cambrian-payout-existing")
            .Returns("tr_recovered");

        var result = await _sut.RequestAsync(new PayoutRequest { Amount = 50m }, "c1");

        Assert.Equal("completed", result.Status);
        Assert.Equal(pending.Id, result.Id);
        await _wallet.DidNotReceive().AddTransactionAsync(Arg.Any<WalletTransaction>());
        await _payouts.DidNotReceive().AddAsync(Arg.Any<Payout>());
        await _payouts.Received().UpdateAsync(
            Arg.Is<Payout>(p => p.StripeTransferId == "tr_recovered" && p.Status == "completed"));
    }

    [Fact]
    public async Task RequestAsync_RejectsDifferentAmount_WhilePayoutIsPending()
    {
        var user = new ApplicationUser { Id = "c1", StripeAccountId = "acct_1" };
        _users.FindByIdAsync("c1").Returns(user);
        _gateway.GetConnectAccountStatusAsync("acct_1").Returns(new ConnectAccountStatus
            { AccountId = "acct_1", Status = "active", ChargesEnabled = true, PayoutsEnabled = true });
        _payouts.GetOutstandingAsync("c1").Returns(new Payout
        {
            Id = Guid.NewGuid(),
            CreatorId = "c1",
            AmountCents = 5000,
            Status = "pending"
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RequestAsync(new PayoutRequest { Amount = 60m }, "c1"));

        Assert.Contains("already processing", ex.Message);
        await _wallet.DidNotReceive().AddTransactionAsync(Arg.Any<WalletTransaction>());
        await _gateway.DidNotReceive().CreateTransferAsync(
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>());
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
