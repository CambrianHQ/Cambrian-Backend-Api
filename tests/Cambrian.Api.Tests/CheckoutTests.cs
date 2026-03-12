using System.Security.Claims;
using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Additional checkout edge-case tests complementing CheckoutServiceTests.
/// </summary>
public sealed class CheckoutTests
{
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly CheckoutService _sut;

    public CheckoutTests()
    {
        var config = Substitute.For<IConfiguration>();
        config["App:FrontendUrl"].Returns("http://localhost:5173");
        var purchases = Substitute.For<IPurchaseRepository>();
        var library = Substitute.For<ILibraryRepository>();
        var wallet = Substitute.For<IWalletRepository>();
        var licenseService = Substitute.For<ILicenseService>();
        var logger = Substitute.For<ILogger<CheckoutService>>();
        _sut = new CheckoutService(_gateway, _tracks, purchases, library, wallet, licenseService, config, logger);
    }

    private static ClaimsPrincipal MakeUser(string userId = "user-1") =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }));

    [Fact]
    public async Task CreateCheckout_ThrowsFormatException_WhenTrackIdNotGuid()
    {
        var request = new CheckoutRequest { TrackId = "not-a-guid", LicenseType = "standard" };

        await Assert.ThrowsAsync<FormatException>(() =>
            _sut.CreateCheckoutAsync(request, MakeUser()));
    }

    [Fact]
    public async Task CreateCheckout_FallsBackToBasePrice_WhenNonExclusivePriceIsZero()
    {
        var trackId = Guid.NewGuid();
        var track = new Track
        {
            Id = trackId,
            Title = "Beat",
            Price = 20,
            ExclusivePriceCents = 5000,
            NonExclusivePriceCents = 0,
            CreatorId = "c1"
        };
        _tracks.GetByIdAsync(trackId).Returns(track);
        _gateway.CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("https://stripe.test/checkout");

        var request = new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "non-exclusive" };
        await _sut.CreateCheckoutAsync(request, MakeUser());

        await _gateway.Received(1).CreateCheckoutSessionAsync(
            2000, "Beat", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task CreateCheckout_ClientRefFormat_ContainsLicenseType()
    {
        var trackId = Guid.NewGuid();
        var track = new Track { Id = trackId, Title = "Beat", Price = 10, CreatorId = "c1" };
        _tracks.GetByIdAsync(trackId).Returns(track);
        _gateway.CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("https://stripe.test/checkout");

        var request = new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "exclusive" };
        await _sut.CreateCheckoutAsync(request, MakeUser("buyer-99"));

        await _gateway.Received(1).CreateCheckoutSessionAsync(
            Arg.Any<int>(), Arg.Any<string>(),
            Arg.Is<string>(s => s == $"buyer-99:{trackId}:exclusive:personal"),
            Arg.Any<string?>(), Arg.Any<string?>());
    }
}
