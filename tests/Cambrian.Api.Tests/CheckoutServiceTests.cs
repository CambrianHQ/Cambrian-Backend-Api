using System.Security.Claims;
using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class CheckoutServiceTests
{
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly CheckoutService _sut;

    public CheckoutServiceTests()
    {
        var config = Substitute.For<IConfiguration>();
        config["App:FrontendUrl"].Returns("http://localhost:5173");
        var purchases = Substitute.For<IPurchaseRepository>();
        purchases.GetByBuyerIdAsync(Arg.Any<string>()).Returns(new List<Purchase>());
        var library = Substitute.For<ILibraryRepository>();
        var wallet = Substitute.For<IWalletRepository>();
        var licenseService = Substitute.For<ILicenseService>();
        var logger = Substitute.For<ILogger<CheckoutService>>();
        _sut = new CheckoutService(_gateway, _tracks, purchases, library, wallet, licenseService, config, logger);
    }

    private static ClaimsPrincipal MakeUser(string userId = "user-1") =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }));

    [Fact]
    public async Task CreateCheckout_ThrowsKeyNotFound_WhenTrackMissing()
    {
        var trackId = Guid.NewGuid();
        _tracks.GetByIdAsync(trackId).Returns((Track?)null);

        var request = new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "standard" };

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _sut.CreateCheckoutAsync(request, MakeUser()));
    }

    [Fact]
    public async Task CreateCheckout_ThrowsInvalidOperation_WhenExclusiveAlreadySold()
    {
        var trackId = Guid.NewGuid();
        var track = new Track
        {
            Id = trackId,
            Title = "Beat",
            Price = 30,
            ExclusiveSold = true,
            CreatorId = "c1"
        };
        _tracks.GetByIdAsync(trackId).Returns(track);

        var request = new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "exclusive" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.CreateCheckoutAsync(request, MakeUser()));

        Assert.Contains("exclusive license", ex.Message);
    }

    [Fact]
    public async Task CreateCheckout_UsesExclusivePrice_WhenLicenseIsExclusive()
    {
        var trackId = Guid.NewGuid();
        var track = new Track
        {
            Id = trackId,
            Title = "Beat",
            Price = 30,
            ExclusivePriceCents = 50000,
            NonExclusivePriceCents = 2999,
            CreatorId = "c1"
        };
        _tracks.GetByIdAsync(trackId).Returns(track);
        _gateway.CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("https://stripe.test/checkout");

        var request = new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "exclusive" };
        await _sut.CreateCheckoutAsync(request, MakeUser());

        await _gateway.Received(1).CreateCheckoutSessionAsync(
            50000, "Beat", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task CreateCheckout_UsesNonExclusivePrice_WhenLicenseIsNonExclusive()
    {
        var trackId = Guid.NewGuid();
        var track = new Track
        {
            Id = trackId,
            Title = "Beat",
            Price = 30,
            ExclusivePriceCents = 50000,
            NonExclusivePriceCents = 2999,
            CreatorId = "c1"
        };
        _tracks.GetByIdAsync(trackId).Returns(track);
        _gateway.CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("https://stripe.test/checkout");

        var request = new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "non-exclusive" };
        await _sut.CreateCheckoutAsync(request, MakeUser());

        await _gateway.Received(1).CreateCheckoutSessionAsync(
            2999, "Beat", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task CreateCheckout_FallsBackToBasePrice_WhenExclusivePriceIsZero()
    {
        var trackId = Guid.NewGuid();
        var track = new Track
        {
            Id = trackId,
            Title = "Beat",
            Price = 25,
            ExclusivePriceCents = 0,
            NonExclusivePriceCents = 0,
            CreatorId = "c1"
        };
        _tracks.GetByIdAsync(trackId).Returns(track);
        _gateway.CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("https://stripe.test/checkout");

        var request = new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "exclusive" };
        await _sut.CreateCheckoutAsync(request, MakeUser());

        await _gateway.Received(1).CreateCheckoutSessionAsync(
            2500, "Beat", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task CreateCheckout_UsesBasePrice_WhenLicenseIsStandard()
    {
        var trackId = Guid.NewGuid();
        var track = new Track
        {
            Id = trackId,
            Title = "Beat",
            Price = 15,
            ExclusivePriceCents = 10000,
            NonExclusivePriceCents = 2000,
            CreatorId = "c1"
        };
        _tracks.GetByIdAsync(trackId).Returns(track);
        _gateway.CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("https://stripe.test/checkout");

        var request = new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "standard" };
        await _sut.CreateCheckoutAsync(request, MakeUser());

        await _gateway.Received(1).CreateCheckoutSessionAsync(
            1500, "Beat", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task CreateCheckout_IncludesUserIdInClientRef()
    {
        var trackId = Guid.NewGuid();
        var track = new Track { Id = trackId, Title = "Beat", Price = 10, CreatorId = "c1" };
        _tracks.GetByIdAsync(trackId).Returns(track);
        _gateway.CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("https://stripe.test/checkout");

        var request = new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "standard" };
        await _sut.CreateCheckoutAsync(request, MakeUser("user-42"));

        await _gateway.Received(1).CreateCheckoutSessionAsync(
            Arg.Any<int>(), Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("user-42") && s.Contains(trackId.ToString())),
            Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task CreateCheckout_ReturnsUrlAndCreatedStatus()
    {
        var trackId = Guid.NewGuid();
        var track = new Track { Id = trackId, Title = "Beat", Price = 10, CreatorId = "c1" };
        _tracks.GetByIdAsync(trackId).Returns(track);
        _gateway.CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("https://stripe.test/session-123");

        var result = await _sut.CreateCheckoutAsync(
            new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "standard" },
            MakeUser());

        Assert.Equal("https://stripe.test/session-123", result.CheckoutUrl);
        Assert.Equal("created", result.Status);
    }
}
