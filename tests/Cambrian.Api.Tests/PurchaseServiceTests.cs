using Cambrian.Application.DTOs.Purchases;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class PurchaseServiceTests
{
    private readonly IPurchaseRepository _purchases = Substitute.For<IPurchaseRepository>();
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly ILibraryRepository _library = Substitute.For<ILibraryRepository>();
    private readonly IInvoiceRepository _invoices = Substitute.For<IInvoiceRepository>();
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly PurchaseService _sut;

    public PurchaseServiceTests()
    {
        // Default: mock gateway returns a paid session for any session ID
        _gateway.GetCheckoutSessionAsync(Arg.Any<string>())
            .Returns(new CheckoutSessionInfo { SessionId = "sess_test", Status = "paid" });

        _sut = new PurchaseService(_purchases, _tracks, _library, _invoices,
            Substitute.For<ILicenseService>(), _gateway, Substitute.For<ILogger<PurchaseService>>());
    }

    private Track MakeTrack(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Title = "Test Beat",
        Price = 29.99m,
        CreatorId = "creator-1",
        Creator = new ApplicationUser { DisplayName = "DJ Creator", Email = "dj@test.com" },
        AudioUrl = "https://cdn.test/beat.mp3"
    };

    [Fact]
    public async Task CreateAsync_ThrowsArgumentException_WhenTrackIdInvalid()
    {
        var request = new PurchaseCreateRequest { TrackId = "not-a-guid" };

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.CreateAsync(request, "user-1"));
    }

    [Fact]
    public async Task CreateAsync_ThrowsKeyNotFound_WhenTrackMissing()
    {
        var trackId = Guid.NewGuid();
        _tracks.GetByIdAsync(trackId).Returns((Track?)null);

        var request = new PurchaseCreateRequest { TrackId = trackId.ToString(), StripeSessionId = "sess_test" };

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _sut.CreateAsync(request, "user-1"));
    }

    [Fact]
    public async Task CreateAsync_ThrowsInvalidOperation_WhenTrackExclusiveSold()
    {
        var trackId = Guid.NewGuid();
        var track = MakeTrack(trackId);
        track.ExclusiveSold = true;
        _tracks.GetByIdAsync(trackId).Returns(track);

        var request = new PurchaseCreateRequest { TrackId = trackId.ToString(), StripeSessionId = "sess_test" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.CreateAsync(request, "user-1"));

        Assert.Contains("exclusive license", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_ThrowsInvalidOperation_WhenDuplicatePurchase()
    {
        var trackId = Guid.NewGuid();
        var track = MakeTrack(trackId);
        _tracks.GetByIdAsync(trackId).Returns(track);
        _purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>
        {
            new() { TrackId = trackId, BuyerId = "user-1", Status = "completed" }
        });

        var request = new PurchaseCreateRequest { TrackId = trackId.ToString(), StripeSessionId = "sess_test" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.CreateAsync(request, "user-1"));
    }

    [Fact]
    public async Task CreateAsync_CreatesPurchaseAndLibraryItemAndInvoice()
    {
        var trackId = Guid.NewGuid();
        var track = MakeTrack(trackId);
        _tracks.GetByIdAsync(trackId).Returns(track);
        _purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());

        var request = new PurchaseCreateRequest
        {
            TrackId = trackId.ToString(),
            LicenseType = "non-exclusive",
            PaymentMethod = "stripe",
            StripeSessionId = "sess_test"
        };

        var result = await _sut.CreateAsync(request, "user-1");

        Assert.Equal(trackId.ToString(), result.TrackId);
        Assert.Equal("Test Beat", result.TrackTitle);
        Assert.Equal(2999, result.AmountCents);
        Assert.Equal("non-exclusive", result.LicenseType);
        Assert.Equal("completed", result.Status);

        await _purchases.Received(1).AddAsync(Arg.Is<Purchase>(p =>
            p.BuyerId == "user-1" &&
            p.TrackId == trackId &&
            p.Status == "completed"));

        await _library.Received(1).AddAsync(Arg.Is<LibraryItem>(l =>
            l.UserId == "user-1" &&
            l.TrackId == trackId &&
            l.Title == "Test Beat"));

        await _invoices.Received(1).AddAsync(Arg.Is<Invoice>(i =>
            i.UserId == "user-1" &&
            i.AmountCents == 2999 &&
            i.Status == "paid"));
    }

    [Fact]
    public async Task CreateAsync_MarksExclusiveSold_WhenLicenseIsExclusive()
    {
        var trackId = Guid.NewGuid();
        var track = MakeTrack(trackId);
        _tracks.GetByIdAsync(trackId).Returns(track);
        _tracks.TryMarkExclusiveSoldAsync(trackId).Returns(true);
        _purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());

        var request = new PurchaseCreateRequest
        {
            TrackId = trackId.ToString(),
            LicenseType = "exclusive",
            StripeSessionId = "sess_test"
        };

        await _sut.CreateAsync(request, "user-1");

        await _tracks.Received(1).TryMarkExclusiveSoldAsync(trackId);
    }

    [Fact]
    public async Task CreateAsync_DoesNotMarkExclusiveSold_WhenNonExclusive()
    {
        var trackId = Guid.NewGuid();
        var track = MakeTrack(trackId);
        _tracks.GetByIdAsync(trackId).Returns(track);
        _purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());

        var request = new PurchaseCreateRequest
        {
            TrackId = trackId.ToString(),
            LicenseType = "non-exclusive",
            StripeSessionId = "sess_test"
        };

        await _sut.CreateAsync(request, "user-1");

        Assert.False(track.ExclusiveSold);
        await _tracks.DidNotReceive().TryMarkExclusiveSoldAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task CreateAsync_DefaultsLicenseToNonExclusive()
    {
        var trackId = Guid.NewGuid();
        var track = MakeTrack(trackId);
        _tracks.GetByIdAsync(trackId).Returns(track);
        _purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());

        var request = new PurchaseCreateRequest { TrackId = trackId.ToString(), StripeSessionId = "sess_test" };

        var result = await _sut.CreateAsync(request, "user-1");

        Assert.Equal("non-exclusive", result.LicenseType);
    }

    [Fact]
    public async Task GetByBuyerAsync_ReturnsEmptyList_WhenNoPurchases()
    {
        _purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());

        var result = await _sut.GetByBuyerAsync("user-1");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetByBuyerAsync_MapsAmountToAmountCents()
    {
        _purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>
        {
            new()
            {
                Id = Guid.NewGuid(),
                TrackId = Guid.NewGuid(),
                AmountCents = 2999,
                LicenseType = "non-exclusive",
                Status = "completed",
                CreatedAt = DateTime.UtcNow
            }
        });

        var result = await _sut.GetByBuyerAsync("user-1");

        Assert.Single(result);
        Assert.Equal(2999, result.First().AmountCents);
    }

    // ── Stripe session verification tests ──

    [Fact]
    public async Task CreateAsync_Throws_WhenNoStripeSessionId()
    {
        var request = new PurchaseCreateRequest
        {
            TrackId = Guid.NewGuid().ToString(),
            StripeSessionId = null
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.CreateAsync(request, "user-1"));
        Assert.Contains("Stripe checkout session ID is required", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenStripeSessionNotPaid()
    {
        _gateway.GetCheckoutSessionAsync("sess_unpaid")
            .Returns(new CheckoutSessionInfo { SessionId = "sess_unpaid", Status = "open" });

        var request = new PurchaseCreateRequest
        {
            TrackId = Guid.NewGuid().ToString(),
            StripeSessionId = "sess_unpaid"
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.CreateAsync(request, "user-1"));
        Assert.Contains("Payment has not been completed", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenStripeSessionNotFound()
    {
        _gateway.GetCheckoutSessionAsync("sess_missing")
            .Returns((CheckoutSessionInfo?)null);

        var request = new PurchaseCreateRequest
        {
            TrackId = Guid.NewGuid().ToString(),
            StripeSessionId = "sess_missing"
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.CreateAsync(request, "user-1"));
        Assert.Contains("Payment has not been completed", ex.Message);
    }
}
