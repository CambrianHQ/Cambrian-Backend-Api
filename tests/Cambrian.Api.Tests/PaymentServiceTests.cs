using Cambrian.Application.DTOs.Payments;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class PaymentServiceTests
{
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly IPurchaseRepository _purchases = Substitute.For<IPurchaseRepository>();
    private readonly ILibraryRepository _library = Substitute.For<ILibraryRepository>();
    private readonly IInvoiceRepository _invoices = Substitute.For<IInvoiceRepository>();
    private readonly PaymentService _sut;

    public PaymentServiceTests()
    {
        _sut = new PaymentService(_gateway, _tracks, _purchases, _library, _invoices);
    }

    [Fact]
    public async Task CreateCheckout_ThrowsArgumentException_WhenTrackIdEmpty()
    {
        var request = new PaymentCheckoutRequest { TrackId = "" };

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.CreateCheckoutAsync(request));
    }

    [Fact]
    public async Task CreateCheckout_ThrowsArgumentException_WhenTrackIdNull()
    {
        var request = new PaymentCheckoutRequest { TrackId = null };

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.CreateCheckoutAsync(request));
    }

    [Fact]
    public async Task CreateCheckout_ThrowsKeyNotFound_WhenTrackMissing()
    {
        var trackId = Guid.NewGuid();
        _tracks.GetByIdAsync(trackId).Returns((Track?)null);

        var request = new PaymentCheckoutRequest { TrackId = trackId.ToString() };

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _sut.CreateCheckoutAsync(request));
    }

    [Fact]
    public async Task CreateCheckout_ConvertsTrackPriceToCents()
    {
        var trackId = Guid.NewGuid();
        var track = new Track { Id = trackId, Title = "Beat", Price = 49.99, CreatorId = "c1" };
        _tracks.GetByIdAsync(trackId).Returns(track);
        _gateway.CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), null, null)
            .Returns("https://stripe.test/session");

        await _sut.CreateCheckoutAsync(new PaymentCheckoutRequest { TrackId = trackId.ToString() });

        await _gateway.Received(1).CreateCheckoutSessionAsync(
            4999, "Beat", Arg.Any<string>(), null, null);
    }

    [Fact]
    public async Task CreateCheckout_UsesClientReferenceIdIfProvided()
    {
        var trackId = Guid.NewGuid();
        var track = new Track { Id = trackId, Title = "Beat", Price = 10, CreatorId = "c1" };
        _tracks.GetByIdAsync(trackId).Returns(track);
        _gateway.CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), null, null)
            .Returns("https://stripe.test/session");

        await _sut.CreateCheckoutAsync(new PaymentCheckoutRequest
        {
            TrackId = trackId.ToString(),
            ClientReferenceId = "custom-ref"
        });

        await _gateway.Received(1).CreateCheckoutSessionAsync(
            1000, "Beat", "custom-ref", null, null);
    }

    [Fact]
    public async Task CreateCheckout_FallsBackToTrackIdAsClientRef()
    {
        var trackId = Guid.NewGuid();
        var track = new Track { Id = trackId, Title = "Beat", Price = 10, CreatorId = "c1" };
        _tracks.GetByIdAsync(trackId).Returns(track);
        _gateway.CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), null, null)
            .Returns("https://stripe.test/session");

        await _sut.CreateCheckoutAsync(new PaymentCheckoutRequest
        {
            TrackId = trackId.ToString(),
            ClientReferenceId = null
        });

        await _gateway.Received(1).CreateCheckoutSessionAsync(
            1000, "Beat", trackId.ToString(), null, null);
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
        Assert.Equal(purchase.Id.ToString(), result.PurchaseId);
    }

    [Fact]
    public async Task GetResult_FlagsDuplicate_WhenMultiplePurchases()
    {
        var trackId = Guid.NewGuid();
        _purchases.GetByTrackIdAsync(trackId).Returns(new List<Purchase>
        {
            new() { Id = Guid.NewGuid(), TrackId = trackId, Status = "completed", BuyerId = "user-1" },
            new() { Id = Guid.NewGuid(), TrackId = trackId, Status = "completed", BuyerId = "user-2" }
        });

        var result = await _sut.GetResultAsync(null, trackId.ToString());

        Assert.True(result.Duplicate);
    }

    [Fact]
    public async Task ProcessAsync_ThrowsKeyNotFound_WhenPurchaseMissing()
    {
        var purchaseId = Guid.NewGuid();
        _purchases.GetByIdAsync(purchaseId).Returns((Purchase?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _sut.ProcessAsync(new PaymentProcessRequest { PurchaseId = purchaseId.ToString() }));
    }

    [Fact]
    public async Task ProcessAsync_UpdatesStatusToCompleted_AndCreatesLibraryAndInvoice()
    {
        var trackId = Guid.NewGuid();
        var track = new Track { Id = trackId, Title = "Beat", CreatorId = "c1", Price = 29.99 };
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            BuyerId = "user-1",
            Amount = 29.99,
            LicenseType = "non-exclusive",
            Status = "pending"
        };
        _purchases.GetByIdAsync(purchase.Id).Returns(purchase);
        _tracks.GetByIdAsync(trackId).Returns(track);
        _library.GetByUserAndTrackAsync("user-1", trackId).Returns((LibraryItem?)null);

        await _sut.ProcessAsync(new PaymentProcessRequest
        {
            PurchaseId = purchase.Id.ToString(),
            PaymentMethodId = "pm_test"
        });

        Assert.Equal("completed", purchase.Status);
        Assert.Equal("pm_test", purchase.PaymentMethod);
        await _purchases.Received(1).UpdateAsync(purchase);
        await _library.Received(1).AddAsync(Arg.Is<LibraryItem>(l =>
            l.UserId == "user-1" && l.TrackId == trackId));
        await _invoices.Received(1).AddAsync(Arg.Is<Invoice>(i =>
            i.UserId == "user-1" && i.PurchaseId == purchase.Id));
    }

    [Fact]
    public async Task ProcessAsync_SkipsLibraryAndInvoice_WhenAlreadyCompleted()
    {
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            TrackId = Guid.NewGuid(),
            BuyerId = "user-1",
            Amount = 10,
            Status = "completed"
        };
        _purchases.GetByIdAsync(purchase.Id).Returns(purchase);

        await _sut.ProcessAsync(new PaymentProcessRequest
        {
            PurchaseId = purchase.Id.ToString(),
            PaymentMethodId = "pm_test"
        });

        await _library.DidNotReceive().AddAsync(Arg.Any<LibraryItem>());
        await _invoices.DidNotReceive().AddAsync(Arg.Any<Invoice>());
    }

    [Fact]
    public async Task ProcessAsync_DefaultsPaymentMethodToStripe()
    {
        var trackId = Guid.NewGuid();
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            BuyerId = "user-1",
            Amount = 10,
            LicenseType = "non-exclusive",
            Status = "pending"
        };
        _purchases.GetByIdAsync(purchase.Id).Returns(purchase);
        _tracks.GetByIdAsync(trackId).Returns(new Track { Id = trackId, Title = "B", CreatorId = "c1" });
        _library.GetByUserAndTrackAsync("user-1", trackId).Returns((LibraryItem?)null);

        await _sut.ProcessAsync(new PaymentProcessRequest
        {
            PurchaseId = purchase.Id.ToString(),
            PaymentMethodId = null
        });

        Assert.Equal("stripe", purchase.PaymentMethod);
    }

    [Fact]
    public async Task ProcessAsync_MarksExclusiveSoldAndHidden_WhenExclusiveLicense()
    {
        var trackId = Guid.NewGuid();
        var track = new Track { Id = trackId, Title = "Beat", CreatorId = "c1", Price = 500, Visibility = "public" };
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            BuyerId = "user-1",
            Amount = 500,
            LicenseType = "exclusive",
            Status = "pending"
        };
        _purchases.GetByIdAsync(purchase.Id).Returns(purchase);
        _tracks.GetByIdAsync(trackId).Returns(track);
        _library.GetByUserAndTrackAsync("user-1", trackId).Returns((LibraryItem?)null);

        await _sut.ProcessAsync(new PaymentProcessRequest
        {
            PurchaseId = purchase.Id.ToString()
        });

        Assert.True(track.ExclusiveSold);
        Assert.Equal("hidden", track.Visibility);
        await _tracks.Received(1).UpdateAsync(track);
    }
}
