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
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly PayoutService _sut;

    public PayoutServiceTests()
    {
        _sut = new PayoutService(_payouts, _purchases, _tracks);
    }

    private void SetupCreatorWithRevenue(string creatorId, Guid trackId, double revenue)
    {
        _tracks.GetByCreatorIdAsync(creatorId).Returns(new List<Track>
        {
            new() { Id = trackId, CreatorId = creatorId, Title = "Beat" }
        });
        _purchases.GetByTrackIdAsync(trackId).Returns(new List<Purchase>
        {
            new() { Id = Guid.NewGuid(), TrackId = trackId, BuyerId = "buyer-1", Amount = revenue, Status = "completed" }
        });
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
    public async Task RequestAsync_ThrowsArgumentException_WhenCreatorIdEmpty()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.RequestAsync(new PayoutRequest { Amount = 10m }, ""));
    }

    [Fact]
    public async Task RequestAsync_CreatesPendingPayout_WhenWithinRevenue()
    {
        var trackId = Guid.NewGuid();
        SetupCreatorWithRevenue("creator-1", trackId, 100.0);
        _payouts.GetByCreatorIdAsync("creator-1").Returns(new List<Payout>());

        var result = await _sut.RequestAsync(new PayoutRequest { Amount = 50.00m }, "creator-1");

        Assert.Equal(50.00m, result.Amount);
        Assert.Equal("pending", result.Status);
        await _payouts.Received(1).AddAsync(Arg.Is<Payout>(p =>
            p.Amount == 50.0 && p.Status == "pending" && p.CreatorId == "creator-1"));
    }

    [Fact]
    public async Task RequestAsync_ThrowsInvalidOperation_WhenExceedsAvailableRevenue()
    {
        var trackId = Guid.NewGuid();
        SetupCreatorWithRevenue("creator-1", trackId, 50.0);
        _payouts.GetByCreatorIdAsync("creator-1").Returns(new List<Payout>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RequestAsync(new PayoutRequest { Amount = 100m }, "creator-1"));

        Assert.Contains("exceeds available balance", ex.Message);
    }

    [Fact]
    public async Task RequestAsync_AccountsForExistingPayouts()
    {
        var trackId = Guid.NewGuid();
        SetupCreatorWithRevenue("creator-1", trackId, 100.0);
        _payouts.GetByCreatorIdAsync("creator-1").Returns(new List<Payout>
        {
            new() { Id = Guid.NewGuid(), CreatorId = "creator-1", Amount = 80, Status = "completed" }
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RequestAsync(new PayoutRequest { Amount = 30m }, "creator-1"));

        Assert.Contains("exceeds available balance", ex.Message);
    }

    [Fact]
    public async Task GetEarningsAsync_ReturnsComputedValues()
    {
        var trackId = Guid.NewGuid();
        SetupCreatorWithRevenue("creator-1", trackId, 100.0);
        _payouts.GetByCreatorIdAsync("creator-1").Returns(new List<Payout>
        {
            new() { Id = Guid.NewGuid(), CreatorId = "creator-1", Amount = 30, Status = "completed" },
            new() { Id = Guid.NewGuid(), CreatorId = "creator-1", Amount = 10, Status = "pending" }
        });

        var result = await _sut.GetEarningsAsync("creator-1");

        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.Contains("\"balance\":100", json);
        Assert.Contains("\"available\":60", json);
    }
}
