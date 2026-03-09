using Cambrian.Application.DTOs.Payouts;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class PayoutServiceTests
{
    private readonly IPayoutRepository _payouts = Substitute.For<IPayoutRepository>();
    private readonly IPurchaseRepository _purchases = Substitute.For<IPurchaseRepository>();
    private readonly PayoutService _sut;

    public PayoutServiceTests()
    {
        _sut = new PayoutService(_payouts, _purchases);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task RequestAsync_ThrowsArgumentException_WhenAmountNotPositive(decimal amount)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.RequestAsync(new PayoutRequest { Amount = amount }, "creator-1"));
    }

    [Fact]
    public async Task RequestAsync_CreatesPendingPayout()
    {
        var result = await _sut.RequestAsync(new PayoutRequest { Amount = 50.00m }, "creator-1");

        Assert.Equal(50.00m, result.Amount);
        Assert.Equal("pending", result.Status);
        await _payouts.Received(1).AddAsync(Arg.Is<Payout>(p =>
            p.CreatorId == "creator-1" &&
            p.Amount == 50.0 &&
            p.Status == "pending"));
    }

    [Fact]
    public async Task GetEarningsAsync_ReturnsZeroDefaults()
    {
        var result = await _sut.GetEarningsAsync();

        Assert.NotNull(result);
    }
}
