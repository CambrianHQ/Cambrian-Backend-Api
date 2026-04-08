using System.Security.Claims;
using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.DTOs.Licenses;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Identity;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests verifying copyright buyout licensing behaviour:
///   1. Non-exclusive allows multiple buyers
///   2. Exclusive prevents second purchase
///   3. Copyright buyout transfers ownership and prevents resale
/// </summary>
public sealed class CopyrightBuyoutTests
{
    // ───────────────────────────── helpers ─────────────────────────────

    private static ClaimsPrincipal MakeUser(string userId = "buyer-1") =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Email, $"{userId}@test.com")
        }));

    private static Track MakeTrack(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Title = "Test Beat",
        CambrianTrackId = "CAMB-TRK-TEST01",
        Price = 30,
        NonExclusivePriceCents = 2999,
        ExclusivePriceCents = 50000,
        CreatorId = "creator-1",
        Status = "available"
    };

    private static CheckoutService BuildService(
        ITrackRepository? tracks = null,
        IPurchaseRepository? purchases = null,
        ILibraryRepository? library = null,
        IWalletRepository? wallet = null,
        ILicenseService? licenseService = null,
        IPaymentGateway? gateway = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FrontendUrl"] = "http://localhost:5173",
                ["Checkout:RequireSubscription"] = "false"
            })
            .Build();
        tracks ??= Substitute.For<ITrackRepository>();
        purchases ??= Substitute.For<IPurchaseRepository>();
        purchases.GetByBuyerIdAsync(Arg.Any<string>()).Returns(new List<Purchase>());
        library ??= Substitute.For<ILibraryRepository>();
        wallet ??= Substitute.For<IWalletRepository>();
        licenseService ??= Substitute.For<ILicenseService>();
        gateway ??= Substitute.For<IPaymentGateway>();
        gateway.CreateCheckoutSessionAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("https://stripe.test/checkout");

        return new CheckoutService(
            gateway, tracks, purchases, library, wallet,
            licenseService, Substitute.For<ITransactionManager>(), Substitute.For<IEmailService>(),
            Substitute.For<ISubscriptionRepository>(), config,
            Substitute.For<UserManager<ApplicationUser>>(Substitute.For<IUserStore<ApplicationUser>>(), null, null, null, null, null, null, null, null),
            Substitute.For<ILogger<CheckoutService>>());
    }

    // ───────── Test 1: Non-exclusive allows multiple buyers ──────────

    [Fact]
    public async Task NonExclusive_AllowsMultipleBuyers()
    {
        var trackId = Guid.NewGuid();
        var track = MakeTrack(trackId);
        var tracks = Substitute.For<ITrackRepository>();
        tracks.GetByIdAsync(trackId).Returns(track);

        var sut = BuildService(tracks: tracks);

        // First buyer
        var r1 = await sut.CreateCheckoutAsync(
            new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "non-exclusive" },
            MakeUser("buyer-1"));

        // Second buyer
        var r2 = await sut.CreateCheckoutAsync(
            new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "non-exclusive" },
            MakeUser("buyer-2"));

        Assert.Equal("created", r1.Status);
        Assert.Equal("created", r2.Status);
    }

    // ───────── Test 2: Exclusive prevents second purchase ────────────

    [Fact]
    public async Task Exclusive_PreventsDuplicatePurchase()
    {
        var trackId = Guid.NewGuid();
        var track = MakeTrack(trackId);
        track.ExclusiveSold = true; // already sold
        var tracks = Substitute.For<ITrackRepository>();
        tracks.GetByIdAsync(trackId).Returns(track);

        var sut = BuildService(tracks: tracks);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateCheckoutAsync(
                new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "exclusive" },
                MakeUser()));

        Assert.Contains("exclusive license", ex.Message);
    }

    // ──── Test 3: Copyright buyout transfers ownership & blocks resale ────

    [Fact]
    public async Task CopyrightBuyout_AllowedWhenTrackAvailable()
    {
        var trackId = Guid.NewGuid();
        var track = MakeTrack(trackId);
        var tracks = Substitute.For<ITrackRepository>();
        tracks.GetByIdAsync(trackId).Returns(track);

        var sut = BuildService(tracks: tracks);

        var result = await sut.CreateCheckoutAsync(
            new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "copyright_buyout" },
            MakeUser());

        Assert.Equal("created", result.Status);
    }

    [Fact]
    public async Task CopyrightBuyout_BlockedWhenExclusiveSold()
    {
        var trackId = Guid.NewGuid();
        var track = MakeTrack(trackId);
        track.ExclusiveSold = true;
        var tracks = Substitute.For<ITrackRepository>();
        tracks.GetByIdAsync(trackId).Returns(track);

        var sut = BuildService(tracks: tracks);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateCheckoutAsync(
                new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "copyright_buyout" },
                MakeUser()));

        Assert.Contains("no longer available", ex.Message);
    }

    [Fact]
    public async Task CopyrightBuyout_BlockedWhenCopyrightAlreadyTransferred()
    {
        var trackId = Guid.NewGuid();
        var track = MakeTrack(trackId);
        track.Status = "copyright_transferred";
        var tracks = Substitute.For<ITrackRepository>();
        tracks.GetByIdAsync(trackId).Returns(track);

        var sut = BuildService(tracks: tracks);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateCheckoutAsync(
                new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "copyright_buyout" },
                MakeUser()));

        Assert.Contains("no longer available", ex.Message);
    }

    [Fact]
    public async Task CopyrightBuyout_UsesExclusivePrice()
    {
        var trackId = Guid.NewGuid();
        var track = MakeTrack(trackId);
        var tracks = Substitute.For<ITrackRepository>();
        tracks.GetByIdAsync(trackId).Returns(track);
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.CreateCheckoutSessionAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("https://stripe.test/checkout");

        var sut = BuildService(tracks: tracks, gateway: gateway);

        await sut.CreateCheckoutAsync(
            new CheckoutRequest { TrackId = trackId.ToString(), LicenseType = "copyright_buyout" },
            MakeUser());

        // Should use the exclusive price (50000 cents)
        await gateway.Received(1).CreateCheckoutSessionAsync(
            50000, "Test Beat", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    // ──── Test: LicenseService sets copyrightOwner correctly ────

    [Fact]
    public async Task LicenseService_CopyrightBuyout_SetsBuyerAsCopyrightOwner()
    {
        var repo = Substitute.For<ILicenseCertificateRepository>();
        repo.GetByPurchaseIdAsync(Arg.Any<Guid>()).Returns((LicenseCertificate?)null);
        var sut = new LicenseService(repo);

        var cert = await sut.IssueCertificateAsync(
            Guid.NewGuid(), "CAMB-TRK-TEST01", "buyer-1", "creator-1",
            "copyright_buyout", "personal");

        Assert.Equal("buyer-1", cert.CopyrightOwner);
        Assert.Contains("Full copyright ownership transfer", cert.AllowedUses!);
        Assert.Contains("Original creator relinquishes all ownership rights", cert.Restrictions!);
    }

    [Fact]
    public async Task LicenseService_Exclusive_SetsCreatorAsCopyrightOwner()
    {
        var repo = Substitute.For<ILicenseCertificateRepository>();
        repo.GetByPurchaseIdAsync(Arg.Any<Guid>()).Returns((LicenseCertificate?)null);
        var sut = new LicenseService(repo);

        var cert = await sut.IssueCertificateAsync(
            Guid.NewGuid(), "CAMB-TRK-TEST01", "buyer-1", "creator-1",
            "exclusive", "personal");

        Assert.Equal("creator-1", cert.CopyrightOwner);
    }

    [Fact]
    public async Task LicenseService_NonExclusive_SetsCreatorAsCopyrightOwner()
    {
        var repo = Substitute.For<ILicenseCertificateRepository>();
        repo.GetByPurchaseIdAsync(Arg.Any<Guid>()).Returns((LicenseCertificate?)null);
        var sut = new LicenseService(repo);

        var cert = await sut.IssueCertificateAsync(
            Guid.NewGuid(), "CAMB-TRK-TEST01", "buyer-1", "creator-1",
            "non-exclusive", "personal");

        Assert.Equal("creator-1", cert.CopyrightOwner);
    }
}
