using Cambrian.Application.DTOs.Payments;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class PaymentServiceTests
{
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly IPurchaseRepository _purchases = Substitute.For<IPurchaseRepository>();
    private readonly ILogger<PaymentService> _logger = Substitute.For<ILogger<PaymentService>>();
    private readonly PaymentService _sut;
    private const string TestUserId = "user-1";

    public PaymentServiceTests()
    {
        _gateway.GetCheckoutSessionAsync(Arg.Any<string>())
            .Returns(new CheckoutSessionInfo { SessionId = "cs_test_session", Status = "paid" });
        _sut = new PaymentService(_gateway, _tracks, _purchases, _logger);
    }

    [Fact]
    public async Task CreateCheckout_ThrowsArgumentException_WhenTrackIdEmpty()
    {
        var request = new PaymentCheckoutRequest { TrackId = "" };

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.CreateCheckoutAsync(request, TestUserId));
    }

    [Fact]
    public async Task CreateCheckout_ThrowsArgumentException_WhenTrackIdNull()
    {
        var request = new PaymentCheckoutRequest { TrackId = null };

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.CreateCheckoutAsync(request, TestUserId));
    }

    [Fact]
    public async Task CreateCheckout_ThrowsArgumentException_WhenUserIdEmpty()
    {
        var request = new PaymentCheckoutRequest { TrackId = Guid.NewGuid().ToString() };

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.CreateCheckoutAsync(request, ""));
    }

    [Fact]
    public async Task CreateCheckout_ThrowsKeyNotFound_WhenTrackMissing()
    {
        var trackId = Guid.NewGuid();
        _tracks.GetByIdAsync(trackId).Returns((Track?)null);

        var request = new PaymentCheckoutRequest { TrackId = trackId.ToString() };

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _sut.CreateCheckoutAsync(request, TestUserId));
    }

    [Fact]
    public async Task CreateCheckout_ConvertsTrackPriceToCents()
    {
        var trackId = Guid.NewGuid();
        var track = new Track { Id = trackId, Title = "Beat", Price = 49.99m, CreatorId = "c1" };
        _tracks.GetByIdAsync(trackId).Returns(track);
        _gateway.CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), null, null)
            .Returns("https://stripe.test/session");

        await _sut.CreateCheckoutAsync(new PaymentCheckoutRequest { TrackId = trackId.ToString() }, TestUserId);

        await _gateway.Received(1).CreateCheckoutSessionAsync(
            4999, "Beat", Arg.Any<string>(), null, null);
    }

    [Fact]
    public async Task CreateCheckout_BuildsStandardizedClientReferenceId()
    {
        var trackId = Guid.NewGuid();
        var track = new Track { Id = trackId, Title = "Beat", Price = 10, CreatorId = "c1" };
        _tracks.GetByIdAsync(trackId).Returns(track);
        _gateway.CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), null, null)
            .Returns("https://stripe.test/session");

        await _sut.CreateCheckoutAsync(new PaymentCheckoutRequest
        {
            TrackId = trackId.ToString(),
            LicenseType = "exclusive",
            UsageType = "commercial"
        }, TestUserId);

        var expectedRef = $"{TestUserId}:{trackId}:exclusive:commercial";
        await _gateway.Received(1).CreateCheckoutSessionAsync(
            1000, "Beat", expectedRef, null, null);
    }

    [Fact]
    public async Task CreateCheckout_DefaultsLicenseAndUsageType()
    {
        var trackId = Guid.NewGuid();
        var track = new Track { Id = trackId, Title = "Beat", Price = 10, CreatorId = "c1" };
        _tracks.GetByIdAsync(trackId).Returns(track);
        _gateway.CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), null, null)
            .Returns("https://stripe.test/session");

        await _sut.CreateCheckoutAsync(new PaymentCheckoutRequest
        {
            TrackId = trackId.ToString()
        }, TestUserId);

        var expectedRef = $"{TestUserId}:{trackId}:non-exclusive:personal";
        await _gateway.Received(1).CreateCheckoutSessionAsync(
            1000, "Beat", expectedRef, null, null);
    }

    [Fact]
    public async Task GetState_ReturnsReadyStatus()
    {
        var result = await _sut.GetStateAsync();

        Assert.Equal("ready", result.Status);
    }

    [Fact]
    public async Task GetResult_ReturnsUnknown_WhenTrackIdMissing()
    {
        var result = await _sut.GetResultAsync(null, null);

        Assert.Equal("unknown", result.Status);
    }

    [Fact]
    public async Task GetResult_ReturnsPassedStatus_WhenNoTrackId()
    {
        var result = await _sut.GetResultAsync("success", null);

        Assert.Equal("success", result.Status);
    }

    [Fact]
    public async Task GetResult_ReturnsLatestPurchaseStatus()
    {
        var trackId = Guid.NewGuid();
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            Status = "completed",
            BuyerId = "user-1"
        };
        _purchases.GetByTrackIdAsync(trackId).Returns(new List<Purchase> { purchase });

        var result = await _sut.GetResultAsync(null, trackId.ToString());

        Assert.Equal("completed", result.Status);
    }

    [Fact]
    public async Task GetResult_DoesNotExposeDuplicate_WhenMultiplePurchases()
    {
        var trackId = Guid.NewGuid();
        _purchases.GetByTrackIdAsync(trackId).Returns(new List<Purchase>
        {
            new() { Id = Guid.NewGuid(), TrackId = trackId, Status = "completed", BuyerId = "user-1" },
            new() { Id = Guid.NewGuid(), TrackId = trackId, Status = "completed", BuyerId = "user-2" }
        });

        var result = await _sut.GetResultAsync(null, trackId.ToString());

        // Security: anonymous endpoint no longer exposes duplicate flag
        Assert.False(result.Duplicate);
    }

    [Fact]
    public async Task ProcessAsync_ThrowsKeyNotFound_WhenPurchaseMissing()
    {
        var purchaseId = Guid.NewGuid();
        _purchases.GetByIdAsync(purchaseId).Returns((Purchase?)null);

#pragma warning disable CS0618 // Testing legacy ProcessAsync intentionally
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _sut.ProcessAsync(new PaymentProcessRequest { PurchaseId = purchaseId.ToString() }, "user-1"));
#pragma warning restore CS0618
    }

    [Fact]
    public async Task ProcessAsync_ThrowsUnauthorized_WhenUserDoesNotOwnPurchase()
    {
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            TrackId = Guid.NewGuid(),
            BuyerId = "user-1",
            Status = "pending"
        };
        _purchases.GetByIdAsync(purchase.Id).Returns(purchase);

#pragma warning disable CS0618 // Testing legacy ProcessAsync intentionally
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _sut.ProcessAsync(new PaymentProcessRequest { PurchaseId = purchase.Id.ToString() }, "user-999"));
#pragma warning restore CS0618
    }

    [Fact]
    public async Task ProcessAsync_UpdatesStatusToCompleted()
    {
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            TrackId = Guid.NewGuid(),
            BuyerId = "user-1",
            Status = "pending",
            StripeSessionId = "cs_test_session"
        };
        _purchases.GetByIdAsync(purchase.Id).Returns(purchase);

#pragma warning disable CS0618 // Testing legacy ProcessAsync intentionally
        await _sut.ProcessAsync(new PaymentProcessRequest
        {
            PurchaseId = purchase.Id.ToString(),
            PaymentMethodId = "pm_test"
        }, "user-1");
#pragma warning restore CS0618

        Assert.Equal("completed", purchase.Status);
        Assert.Equal("pm_test", purchase.PaymentMethod);
        await _purchases.Received(1).UpdateAsync(purchase);
    }

    [Fact]
    public async Task ProcessAsync_DefaultsPaymentMethodToStripe()
    {
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            TrackId = Guid.NewGuid(),
            BuyerId = "user-1",
            Status = "pending",
            StripeSessionId = "cs_test_session"
        };
        _purchases.GetByIdAsync(purchase.Id).Returns(purchase);

#pragma warning disable CS0618 // Testing legacy ProcessAsync intentionally
        await _sut.ProcessAsync(new PaymentProcessRequest
        {
            PurchaseId = purchase.Id.ToString(),
            PaymentMethodId = null
        }, "user-1");
#pragma warning restore CS0618

        Assert.Equal("stripe", purchase.PaymentMethod);
    }
}
