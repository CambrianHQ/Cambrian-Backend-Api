using Cambrian.Application.DTOs.Purchases;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class PurchaseServiceTests
{
    private readonly IPurchaseRepository _purchases = Substitute.For<IPurchaseRepository>();
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly ILibraryRepository _library = Substitute.For<ILibraryRepository>();
    private readonly IInvoiceRepository _invoices = Substitute.For<IInvoiceRepository>();
    private readonly PurchaseService _sut;

    public PurchaseServiceTests()
    {
        _sut = new PurchaseService(_purchases, _tracks, _library, _invoices);
    }

    private Track MakeTrack(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Title = "Test Beat",
        Price = 29.99,
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

        var request = new PurchaseCreateRequest { TrackId = trackId.ToString() };

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _sut.CreateAsync(request, "user-1"));
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

        var request = new PurchaseCreateRequest { TrackId = trackId.ToString() };

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
            PaymentMethod = "stripe"
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
        _purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());

        var request = new PurchaseCreateRequest
        {
            TrackId = trackId.ToString(),
            LicenseType = "exclusive"
        };

        await _sut.CreateAsync(request, "user-1");

        Assert.True(track.ExclusiveSold);
        await _tracks.Received(1).UpdateAsync(track);
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
            LicenseType = "non-exclusive"
        };

        await _sut.CreateAsync(request, "user-1");

        Assert.False(track.ExclusiveSold);
        await _tracks.DidNotReceive().UpdateAsync(Arg.Any<Track>());
    }

    [Fact]
    public async Task CreateAsync_DefaultsLicenseToNonExclusive()
    {
        var trackId = Guid.NewGuid();
        var track = MakeTrack(trackId);
        _tracks.GetByIdAsync(trackId).Returns(track);
        _purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());

        var request = new PurchaseCreateRequest { TrackId = trackId.ToString() };

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
                Amount = 29.99,
                LicenseType = "non-exclusive",
                Status = "completed",
                CreatedAt = DateTime.UtcNow
            }
        });

        var result = await _sut.GetByBuyerAsync("user-1");

        Assert.Single(result);
        Assert.Equal(2999, result.First().AmountCents);
    }
}
