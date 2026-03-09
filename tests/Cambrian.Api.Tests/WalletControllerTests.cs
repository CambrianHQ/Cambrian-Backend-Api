using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Wallet;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests for WalletController covering balance retrieval, withdrawal
/// with insufficient-balance guard, and transaction history.
/// </summary>
public sealed class WalletControllerTests
{
    private readonly IWalletRepository _wallet = Substitute.For<IWalletRepository>();
    private readonly WalletController _controller;

    public WalletControllerTests()
    {
        _controller = new WalletController(_wallet);
    }

    private void SetupUser(string userId = "user-1")
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId)
        }, "Test"));
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    // ── Get balance ──

    [Fact]
    public async Task Get_ReturnsBalance()
    {
        SetupUser();
        _wallet.GetBalanceAsync("user-1").Returns(5000L);

        var result = await _controller.Get();

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<WalletResponse>>(ok.Value);
        Assert.Equal(5000, envelope.Data!.BalanceCents);
        Assert.Equal("usd", envelope.Data.Currency);
    }

    [Fact]
    public async Task Get_ReturnsZero_WhenNoTransactions()
    {
        SetupUser();
        _wallet.GetBalanceAsync("user-1").Returns(0L);

        var result = await _controller.Get();

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<WalletResponse>>(ok.Value);
        Assert.Equal(0, envelope.Data!.BalanceCents);
    }

    // ── Withdrawal ──

    [Fact]
    public async Task Withdraw_Returns400_WhenInsufficientBalance()
    {
        SetupUser();
        _wallet.GetBalanceAsync("user-1").Returns(1000L);

        var result = await _controller.Withdraw(new WithdrawRequest { Amount = 50.00 });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(bad.Value);
        Assert.Contains("Insufficient", envelope.Error);
    }

    [Fact]
    public async Task Withdraw_Succeeds_WhenBalanceIsSufficient()
    {
        SetupUser();
        _wallet.GetBalanceAsync("user-1").Returns(10000L);

        var result = await _controller.Withdraw(new WithdrawRequest { Amount = 50.00 });

        Assert.IsType<OkObjectResult>(result);
        await _wallet.Received(1).AddTransactionAsync(Arg.Is<WalletTransaction>(t =>
            t.UserId == "user-1" &&
            t.AmountCents == -5000 &&
            t.Type == "withdrawal"));
    }

    [Fact]
    public async Task Withdraw_Succeeds_WhenAmountEqualsBalance()
    {
        SetupUser();
        _wallet.GetBalanceAsync("user-1").Returns(5000L);

        var result = await _controller.Withdraw(new WithdrawRequest { Amount = 50.00 });

        Assert.IsType<OkObjectResult>(result);
    }

    // ── History ──

    [Fact]
    public async Task History_ReturnsList()
    {
        SetupUser();
        _wallet.GetHistoryAsync("user-1", 20).Returns(new List<WalletTransaction>
        {
            new()
            {
                Id = Guid.NewGuid(),
                UserId = "user-1",
                AmountCents = 2999,
                Type = "purchase_credit",
                Description = "Sale of Beat"
            }
        });

        var result = await _controller.History();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task History_ReturnsEmptyList_WhenNoTransactions()
    {
        SetupUser();
        _wallet.GetHistoryAsync("user-1", 20).Returns(new List<WalletTransaction>());

        var result = await _controller.History();

        Assert.IsType<OkObjectResult>(result);
    }
}
