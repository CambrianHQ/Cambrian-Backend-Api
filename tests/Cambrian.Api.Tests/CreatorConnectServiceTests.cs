using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

public sealed class CreatorConnectServiceTests
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();
    private readonly CreatorConnectService _sut;

    public CreatorConnectServiceTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        _users = Substitute.For<UserManager<ApplicationUser>>(
            store, null, null, null, null, null, null, null, null);

        _config["App:FrontendUrl"].Returns("http://localhost:5173");

        _sut = new CreatorConnectService(
            _users, _gateway, _config,
            Substitute.For<ILogger<CreatorConnectService>>());
    }

    // ── StartOnboardingAsync ──

    [Fact]
    public async Task StartOnboarding_CreatesAccount_WhenNoExistingAccount()
    {
        var user = new ApplicationUser { Id = "u1", Email = "creator@test.com", StripeAccountId = null };
        _users.FindByIdAsync("u1").Returns(user);
        _gateway.CreateConnectAccountAsync("creator@test.com").Returns("acct_new");
        _gateway.CreateAccountOnboardingLinkAsync("acct_new", Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://connect.stripe.com/onboarding");
        _users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);

        var result = await _sut.StartOnboardingAsync("u1");

        Assert.Equal("https://connect.stripe.com/onboarding", result.ConnectUrl);
        Assert.Equal("pending", result.Status);
        Assert.Equal("acct_new", user.StripeAccountId);
    }

    [Fact]
    public async Task StartOnboarding_ReusesExistingAccount()
    {
        var user = new ApplicationUser { Id = "u1", Email = "creator@test.com", StripeAccountId = "acct_existing" };
        _users.FindByIdAsync("u1").Returns(user);
        _gateway.CreateAccountOnboardingLinkAsync("acct_existing", Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://connect.stripe.com/onboarding");

        var result = await _sut.StartOnboardingAsync("u1");

        await _gateway.DidNotReceive().CreateConnectAccountAsync(Arg.Any<string>());
        Assert.NotNull(result.ConnectUrl);
    }

    [Fact]
    public async Task StartOnboarding_Throws_WhenUserNotFound()
    {
        _users.FindByIdAsync("missing").Returns((ApplicationUser?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _sut.StartOnboardingAsync("missing"));
    }

    // ── GetStatusAsync ──

    [Fact]
    public async Task GetStatus_ReturnsNotConnected_WhenNoStripeAccount()
    {
        var user = new ApplicationUser { Id = "u1", StripeAccountId = null };
        _users.FindByIdAsync("u1").Returns(user);

        var status = await _sut.GetStatusAsync("u1");

        Assert.False(status.Connected);
        Assert.Equal("not_connected", status.Status);
    }

    [Fact]
    public async Task GetStatus_ReturnsActive_WhenAccountFullyOnboarded()
    {
        var user = new ApplicationUser { Id = "u1", StripeAccountId = "acct_1" };
        _users.FindByIdAsync("u1").Returns(user);
        _gateway.GetConnectAccountStatusAsync("acct_1").Returns(new ConnectAccountStatus
        {
            AccountId = "acct_1",
            Status = "active",
            ChargesEnabled = true,
            PayoutsEnabled = true
        });

        var status = await _sut.GetStatusAsync("u1");

        Assert.True(status.Connected);
        Assert.Equal("active", status.Status);
        Assert.Equal("acct_1", status.AccountId);
    }

    [Fact]
    public async Task GetStatus_ReturnsPending_WhenStripeApiErrors()
    {
        var user = new ApplicationUser { Id = "u1", StripeAccountId = "acct_bad" };
        _users.FindByIdAsync("u1").Returns(user);
        _gateway.GetConnectAccountStatusAsync("acct_bad")
            .Throws(new Exception("Stripe error"));

        var status = await _sut.GetStatusAsync("u1");

        Assert.False(status.Connected);
        Assert.Equal("pending", status.Status);
    }

    // ── GetDashboardLinkAsync ──

    [Fact]
    public async Task GetDashboardLink_ReturnsNull_WhenNoAccount()
    {
        var user = new ApplicationUser { Id = "u1", StripeAccountId = null };
        _users.FindByIdAsync("u1").Returns(user);

        var url = await _sut.GetDashboardLinkAsync("u1");

        Assert.Null(url);
    }

    [Fact]
    public async Task GetDashboardLink_ReturnsNull_WhenAccountNotActive()
    {
        var user = new ApplicationUser { Id = "u1", StripeAccountId = "acct_1" };
        _users.FindByIdAsync("u1").Returns(user);
        _gateway.GetConnectAccountStatusAsync("acct_1").Returns(new ConnectAccountStatus
        {
            AccountId = "acct_1", Status = "pending"
        });

        var url = await _sut.GetDashboardLinkAsync("u1");

        Assert.Null(url);
    }

    [Fact]
    public async Task GetDashboardLink_ReturnsUrl_WhenAccountActive()
    {
        var user = new ApplicationUser { Id = "u1", StripeAccountId = "acct_1" };
        _users.FindByIdAsync("u1").Returns(user);
        _gateway.GetConnectAccountStatusAsync("acct_1").Returns(new ConnectAccountStatus
        {
            AccountId = "acct_1", Status = "active", ChargesEnabled = true, PayoutsEnabled = true
        });
        _gateway.CreateExpressDashboardLinkAsync("acct_1")
            .Returns("https://dashboard.stripe.com/express");

        var url = await _sut.GetDashboardLinkAsync("u1");

        Assert.Equal("https://dashboard.stripe.com/express", url);
    }

    // ── DisconnectAsync ──

    [Fact]
    public async Task Disconnect_ClearsStripeAccountId()
    {
        var user = new ApplicationUser { Id = "u1", StripeAccountId = "acct_1" };
        _users.FindByIdAsync("u1").Returns(user);
        _users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);

        await _sut.DisconnectAsync("u1");

        Assert.Null(user.StripeAccountId);
        await _gateway.Received(1).DeleteConnectedAccountAsync("acct_1");
    }

    [Fact]
    public async Task Disconnect_IsIdempotent_WhenAlreadyDisconnected()
    {
        var user = new ApplicationUser { Id = "u1", StripeAccountId = null };
        _users.FindByIdAsync("u1").Returns(user);

        await _sut.DisconnectAsync("u1"); // Should not throw

        await _gateway.DidNotReceive().DeleteConnectedAccountAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Disconnect_ClearsLocalRef_EvenWhenStripeFails()
    {
        var user = new ApplicationUser { Id = "u1", StripeAccountId = "acct_bad" };
        _users.FindByIdAsync("u1").Returns(user);
        _users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);
        _gateway.DeleteConnectedAccountAsync("acct_bad")
            .Throws(new Exception("Stripe error"));

        await _sut.DisconnectAsync("u1"); // Should not throw

        Assert.Null(user.StripeAccountId);
    }
}
