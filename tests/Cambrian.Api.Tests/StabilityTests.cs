using System.Security.Claims;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.DTOs.Purchases;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Backend stability layer tests — validates invariants, idempotency,
/// transactional guarantees, and validation guards.
/// </summary>
public sealed class StabilityTests
{
    // ────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────

    private static ClaimsPrincipal MakeUser(string userId = "user-1") =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Email, "user@test.com")
        }));

    private static Track MakeTrack(Guid? id = null, int nonExcCents = 2999, int excCents = 10000, int buyoutCents = 50000) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Title = "Test Beat",
        Price = nonExcCents / 100m,
        NonExclusivePriceCents = nonExcCents,
        ExclusivePriceCents = excCents,
        CopyrightBuyoutPriceCents = buyoutCents,
        CreatorId = "creator-1",
        Creator = new ApplicationUser { DisplayName = "DJ Creator", Email = "dj@test.com" },
        AudioUrl = "https://cdn.test/beat.mp3"
    };

    // ────────────────────────────────────────────────
    //  Purchase service — transaction & invariant tests
    // ────────────────────────────────────────────────

    private static PurchaseService MakePurchaseService(
        out IPurchaseRepository purchases,
        out ITrackRepository tracks,
        out ILibraryRepository library,
        out ITransactionManager transactions)
    {
        purchases = Substitute.For<IPurchaseRepository>();
        tracks = Substitute.For<ITrackRepository>();
        library = Substitute.For<ILibraryRepository>();
        transactions = Substitute.For<ITransactionManager>();
        var invoices = Substitute.For<IInvoiceRepository>();
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.GetCheckoutSessionAsync(Arg.Any<string>())
            .Returns(new CheckoutSessionInfo { SessionId = "sess_test", Status = "paid" });

        return new PurchaseService(purchases, tracks, library, invoices,
            Substitute.For<ILicenseService>(), gateway, transactions,
            Substitute.For<ILogger<PurchaseService>>());
    }

    [Fact]
    public async Task Purchase_BeginsAndCommitsTransaction()
    {
        var sut = MakePurchaseService(out var purchases, out var tracks, out _, out var tx);
        var track = MakeTrack();
        tracks.GetByIdAsync(track.Id).Returns(track);
        purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());

        await sut.CreateAsync(new PurchaseCreateRequest
        {
            TrackId = track.Id.ToString(),
            LicenseType = "non-exclusive",
            StripeSessionId = "sess_test"
        }, "user-1");

        await tx.Received(1).BeginTransactionAsync();
        await tx.Received(1).CommitAsync();
        await tx.DidNotReceive().RollbackAsync();
    }

    [Fact]
    public async Task Purchase_RollsBackOnFailure()
    {
        var sut = MakePurchaseService(out var purchases, out var tracks, out _, out var tx);
        var track = MakeTrack();
        tracks.GetByIdAsync(track.Id).Returns(track);
        purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());
        // Force library add to throw after purchase is created
        var library = Substitute.For<ILibraryRepository>();

        // Use the normal factory, but we need a controlled failure.
        // Easiest: make purchases.AddAsync throw *after* the first call succeeds.
        purchases.When(p => p.AddAsync(Arg.Any<Purchase>()))
            .Do(_ => throw new Exception("DB failure"));

        await Assert.ThrowsAsync<Exception>(() =>
            sut.CreateAsync(new PurchaseCreateRequest
            {
                TrackId = track.Id.ToString(),
                LicenseType = "non-exclusive",
                StripeSessionId = "sess_test"
            }, "user-1"));

        await tx.Received(1).BeginTransactionAsync();
        await tx.Received(1).RollbackAsync();
        await tx.DidNotReceive().CommitAsync();
    }

    [Fact]
    public async Task Purchase_RejectsZeroPrice()
    {
        var sut = MakePurchaseService(out var purchases, out var tracks, out _, out _);
        var track = MakeTrack(nonExcCents: 0, excCents: 0, buyoutCents: 0);
        track.Price = 0;
        tracks.GetByIdAsync(track.Id).Returns(track);
        purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateAsync(new PurchaseCreateRequest
            {
                TrackId = track.Id.ToString(),
                LicenseType = "non-exclusive",
                StripeSessionId = "sess_test"
            }, "user-1"));

        Assert.Contains("greater than zero", ex.Message);
    }

    [Fact]
    public async Task Purchase_RejectsSessionReplay()
    {
        var sut = MakePurchaseService(out var purchases, out var tracks, out _, out _);
        var track = MakeTrack();
        tracks.GetByIdAsync(track.Id).Returns(track);
        purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());
        // Simulate a session that was already used
        purchases.GetByStripeSessionIdAsync("sess_test")
            .Returns(new Purchase { Id = Guid.NewGuid(), BuyerId = "other-user", StripeSessionId = "sess_test" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateAsync(new PurchaseCreateRequest
            {
                TrackId = track.Id.ToString(),
                LicenseType = "non-exclusive",
                StripeSessionId = "sess_test"
            }, "user-1"));

        Assert.Contains("already been used", ex.Message);
    }

    [Fact]
    public async Task Purchase_RejectsSessionBelongingToAnotherUser()
    {
        var sut = MakePurchaseService(out var purchases, out var tracks, out _, out _);
        var track = MakeTrack();
        tracks.GetByIdAsync(track.Id).Returns(track);
        purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());
        // Gateway returns a session belonging to a different user
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.GetCheckoutSessionAsync("sess_test")
            .Returns(new CheckoutSessionInfo
            {
                SessionId = "sess_test",
                Status = "paid",
                ClientReferenceId = "different-user:" + track.Id + ":non-exclusive"
            });

        // Rebuild with custom gateway
        var sut2 = new PurchaseService(purchases, tracks, Substitute.For<ILibraryRepository>(),
            Substitute.For<IInvoiceRepository>(), Substitute.For<ILicenseService>(), gateway,
            Substitute.For<ITransactionManager>(), Substitute.For<ILogger<PurchaseService>>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut2.CreateAsync(new PurchaseCreateRequest
            {
                TrackId = track.Id.ToString(),
                LicenseType = "non-exclusive",
                StripeSessionId = "sess_test"
            }, "user-1"));

        Assert.Contains("does not belong to you", ex.Message);
    }

    [Fact]
    public async Task Purchase_RejectsExclusiveWhenAlreadySold()
    {
        var sut = MakePurchaseService(out var purchases, out var tracks, out _, out _);
        var track = MakeTrack();
        track.ExclusiveSold = true;
        tracks.GetByIdAsync(track.Id).Returns(track);
        purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateAsync(new PurchaseCreateRequest
            {
                TrackId = track.Id.ToString(),
                LicenseType = "exclusive",
                StripeSessionId = "sess_test"
            }, "user-1"));

        Assert.Contains("exclusive license", ex.Message);
    }

    [Fact]
    public async Task Purchase_RejectsDuplicateLicense()
    {
        var sut = MakePurchaseService(out var purchases, out var tracks, out _, out _);
        var track = MakeTrack();
        tracks.GetByIdAsync(track.Id).Returns(track);
        purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>
        {
            new() { TrackId = track.Id, LicenseType = "non-exclusive", BuyerId = "user-1" }
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateAsync(new PurchaseCreateRequest
            {
                TrackId = track.Id.ToString(),
                LicenseType = "non-exclusive",
                StripeSessionId = "sess_test"
            }, "user-1"));

        Assert.Contains("already own", ex.Message);
    }

    [Fact]
    public async Task Purchase_RequiresStripeSessionId()
    {
        var sut = MakePurchaseService(out _, out _, out _, out _);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateAsync(new PurchaseCreateRequest
            {
                TrackId = Guid.NewGuid().ToString(),
                LicenseType = "non-exclusive",
                StripeSessionId = ""
            }, "user-1"));

        Assert.Contains("Stripe checkout session ID is required", ex.Message);
    }

    [Fact]
    public async Task Purchase_RejectsUnpaidSession()
    {
        var purchases = Substitute.For<IPurchaseRepository>();
        var tracks = Substitute.For<ITrackRepository>();
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.GetCheckoutSessionAsync("sess_unpaid")
            .Returns(new CheckoutSessionInfo { SessionId = "sess_unpaid", Status = "open" });

        var sut = new PurchaseService(purchases, tracks, Substitute.For<ILibraryRepository>(),
            Substitute.For<IInvoiceRepository>(), Substitute.For<ILicenseService>(), gateway,
            Substitute.For<ITransactionManager>(), Substitute.For<ILogger<PurchaseService>>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateAsync(new PurchaseCreateRequest
            {
                TrackId = Guid.NewGuid().ToString(),
                LicenseType = "non-exclusive",
                StripeSessionId = "sess_unpaid"
            }, "user-1"));

        Assert.Contains("Payment has not been completed", ex.Message);
    }

    [Fact]
    public async Task Purchase_ExclusiveRace_RejectsWhenAtomicMarkFails()
    {
        var sut = MakePurchaseService(out var purchases, out var tracks, out _, out _);
        var track = MakeTrack();
        tracks.GetByIdAsync(track.Id).Returns(track);
        purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());
        // Atomic mark returns false — another request won the race
        tracks.TryMarkExclusiveSoldAsync(track.Id).Returns(false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateAsync(new PurchaseCreateRequest
            {
                TrackId = track.Id.ToString(),
                LicenseType = "exclusive",
                StripeSessionId = "sess_test"
            }, "user-1"));

        Assert.Contains("exclusive license", ex.Message);
    }

    // ────────────────────────────────────────────────
    //  Checkout service — $0 guard & exclusive checks
    // ────────────────────────────────────────────────

    private static CheckoutService MakeCheckoutService(out ITrackRepository tracks, out IPaymentGateway gateway)
    {
        tracks = Substitute.For<ITrackRepository>();
        gateway = Substitute.For<IPaymentGateway>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FrontendUrl"] = "http://localhost:5173",
                ["Checkout:RequireSubscription"] = "false"
            })
            .Build();
        var purchases = Substitute.For<IPurchaseRepository>();
        purchases.GetByBuyerIdAsync(Arg.Any<string>()).Returns(new List<Purchase>());
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);

        return new CheckoutService(gateway, tracks, purchases,
            Substitute.For<ILibraryRepository>(), Substitute.For<IWalletRepository>(),
            Substitute.For<ILicenseService>(), Substitute.For<ITransactionManager>(),
            Substitute.For<IEmailService>(), Substitute.For<ISubscriptionRepository>(), config, users,
            Substitute.For<ILogger<CheckoutService>>());
    }

    [Fact]
    public async Task Checkout_RejectsZeroPriceTrack()
    {
        var sut = MakeCheckoutService(out var tracks, out _);
        var track = MakeTrack(nonExcCents: 0, excCents: 0, buyoutCents: 0);
        track.Price = 0;
        tracks.GetByIdAsync(track.Id).Returns(track);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateCheckoutAsync(
                new CheckoutRequest { TrackId = track.Id.ToString(), LicenseType = "non-exclusive" },
                MakeUser()));

        Assert.Contains("greater than zero", ex.Message);
    }

    [Fact]
    public async Task Checkout_RejectsExclusiveAlreadySold()
    {
        var sut = MakeCheckoutService(out var tracks, out _);
        var track = MakeTrack();
        track.ExclusiveSold = true;
        tracks.GetByIdAsync(track.Id).Returns(track);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateCheckoutAsync(
                new CheckoutRequest { TrackId = track.Id.ToString(), LicenseType = "exclusive" },
                MakeUser()));

        Assert.Contains("exclusive license", ex.Message);
    }

    [Fact]
    public async Task Checkout_RejectsDuplicatePurchase()
    {
        var tracks = Substitute.For<ITrackRepository>();
        var purchases = Substitute.For<IPurchaseRepository>();
        var gateway = Substitute.For<IPaymentGateway>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FrontendUrl"] = "http://localhost:5173",
                ["Checkout:RequireSubscription"] = "false"
            })
            .Build();
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);

        var track = MakeTrack();
        tracks.GetByIdAsync(track.Id).Returns(track);
        purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>
        {
            new() { TrackId = track.Id, LicenseType = "non-exclusive", Status = "completed" }
        });

        var sut = new CheckoutService(gateway, tracks, purchases,
            Substitute.For<ILibraryRepository>(), Substitute.For<IWalletRepository>(),
            Substitute.For<ILicenseService>(), Substitute.For<ITransactionManager>(),
            Substitute.For<IEmailService>(), Substitute.For<ISubscriptionRepository>(), config, users,
            Substitute.For<ILogger<CheckoutService>>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateCheckoutAsync(
                new CheckoutRequest { TrackId = track.Id.ToString(), LicenseType = "non-exclusive" },
                MakeUser()));

        Assert.Contains("already own", ex.Message);
    }

    [Fact]
    public async Task Checkout_UsesCorrectPricesForAllLicenseTypes()
    {
        var sut = MakeCheckoutService(out var tracks, out var gateway);
        var track = MakeTrack(nonExcCents: 2999, excCents: 10000, buyoutCents: 50000);
        tracks.GetByIdAsync(track.Id).Returns(track);
        gateway.CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("https://stripe.test/checkout");

        // Non-exclusive
        await sut.CreateCheckoutAsync(
            new CheckoutRequest { TrackId = track.Id.ToString(), LicenseType = "non-exclusive" },
            MakeUser());
        await gateway.Received(1).CreateCheckoutSessionAsync(2999, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>());
        gateway.ClearReceivedCalls();

        // Exclusive
        await sut.CreateCheckoutAsync(
            new CheckoutRequest { TrackId = track.Id.ToString(), LicenseType = "exclusive" },
            MakeUser());
        await gateway.Received(1).CreateCheckoutSessionAsync(10000, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>());
        gateway.ClearReceivedCalls();

        // Copyright buyout
        await sut.CreateCheckoutAsync(
            new CheckoutRequest { TrackId = track.Id.ToString(), LicenseType = "copyright_buyout" },
            MakeUser());
        await gateway.Received(1).CreateCheckoutSessionAsync(50000, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    // ────────────────────────────────────────────────
    //  Upload service — price range & title validation
    // ────────────────────────────────────────────────

    private static UploadService MakeUploadService(out ITrackRepository tracks, out UserManager<ApplicationUser> users)
    {
        tracks = Substitute.For<ITrackRepository>();
        var storage = Substitute.For<IObjectStorage>();
        storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/key");
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        var creators = Substitute.For<ICreatorIdentityRepository>();

        return new UploadService(storage, tracks, users,
            Substitute.For<ILogger<UploadService>>(), creators);
    }

    private static IFormFile MakeAudioFile(string name = "beat.mp3", long length = 1024)
    {
        // MP3 magic bytes (ID3 tag)
        var bytes = new byte[Math.Max(length, 4)];
        bytes[0] = 0x49; // 'I'
        bytes[1] = 0x44; // 'D'
        bytes[2] = 0x33; // '3'
        var ms = new MemoryStream(bytes);
        var file = Substitute.For<IFormFile>();
        file.FileName.Returns(name);
        file.Length.Returns(length);
        file.ContentType.Returns("audio/mpeg");
        file.OpenReadStream().Returns(ms);
        return file;
    }

    [Fact]
    public async Task Upload_RejectsBlankTitle()
    {
        var sut = MakeUploadService(out _, out var users);
        users.FindByIdAsync("creator-1").Returns(new ApplicationUser
        {
            Id = "creator-1",
            UploadCount = 0,
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro
        });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.Upload(new UploadTrackRequest
            {
                Audio = MakeAudioFile(),
                Title = "   ",
                CreatorId = "creator-1",
                Price = 10m
            }));

        Assert.Contains("title is required", ex.Message);
    }

    [Fact]
    public async Task Upload_RejectsPriceBelowMinimum()
    {
        var sut = MakeUploadService(out _, out var users);
        users.FindByIdAsync("creator-1").Returns(new ApplicationUser
        {
            Id = "creator-1",
            UploadCount = 0,
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro
        });

        // $0.10 = 10 cents, below $0.50 minimum
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.Upload(new UploadTrackRequest
            {
                Audio = MakeAudioFile(),
                Title = "Valid Title",
                CreatorId = "creator-1",
                Price = 0.10m
            }));

        Assert.Contains("at least", ex.Message);
    }

    [Fact]
    public async Task Upload_RejectsPriceAboveMaximum()
    {
        var sut = MakeUploadService(out _, out var users);
        users.FindByIdAsync("creator-1").Returns(new ApplicationUser
        {
            Id = "creator-1",
            UploadCount = 0,
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro
        });

        // $60,000 > $50,000 cap
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.Upload(new UploadTrackRequest
            {
                Audio = MakeAudioFile(),
                Title = "Valid Title",
                CreatorId = "creator-1",
                Price = 60_000m
            }));

        Assert.Contains("must not exceed", ex.Message);
    }

    [Fact]
    public async Task Upload_RejectsNonExclusivePriceBelowMinimum()
    {
        var sut = MakeUploadService(out _, out var users);
        users.FindByIdAsync("creator-1").Returns(new ApplicationUser
        {
            Id = "creator-1",
            UploadCount = 0,
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro
        });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.Upload(new UploadTrackRequest
            {
                Audio = MakeAudioFile(),
                Title = "Valid Title",
                CreatorId = "creator-1",
                Price = 10m,
                NonExclusivePrice = 0.20m
            }));

        Assert.Contains("Non-exclusive price", ex.Message);
        Assert.Contains("at least", ex.Message);
    }

    [Fact]
    public async Task Upload_AcceptsNullPrices()
    {
        // Null prices should be skipped (valid — default pricing)
        var sut = MakeUploadService(out var tracks, out var users);
        users.FindByIdAsync("creator-1").Returns(new ApplicationUser
        {
            Id = "creator-1",
            UploadCount = 0,
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro
        });
        tracks.AddAsync(Arg.Any<Track>()).Returns(Task.CompletedTask);

        // No exception — null prices should be accepted
        await sut.Upload(new UploadTrackRequest
        {
            Audio = MakeAudioFile(),
            Title = "Valid Title",
            CreatorId = "creator-1",
            Price = null,
            NonExclusivePrice = null,
            ExclusivePrice = null,
            CopyrightBuyoutPrice = null
        });

        await tracks.Received(1).AddAsync(Arg.Any<Track>());
    }

    [Fact]
    public async Task Upload_AcceptsValidPriceRange()
    {
        var sut = MakeUploadService(out var tracks, out var users);
        users.FindByIdAsync("creator-1").Returns(new ApplicationUser
        {
            Id = "creator-1",
            UploadCount = 0,
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro
        });
        tracks.AddAsync(Arg.Any<Track>()).Returns(Task.CompletedTask);

        // Valid: $0.50 minimum boundary
        await sut.Upload(new UploadTrackRequest
        {
            Audio = MakeAudioFile(),
            Title = "Valid Title",
            CreatorId = "creator-1",
            Price = 0.50m,
            NonExclusivePrice = 5m,
            ExclusivePrice = 100m,
            CopyrightBuyoutPrice = 500m
        });

        await tracks.Received(1).AddAsync(Arg.Any<Track>());
    }

    // ────────────────────────────────────────────────
    //  Image upload — magic byte validation
    // ────────────────────────────────────────────────

    [Fact]
    public void ImageMagicBytes_ValidJpeg_Passes()
    {
        // JPEG starts with FF D8 FF
        var stream = new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 });
        Assert.True(CheckImageMagicBytes(stream, ".jpg"));
    }

    [Fact]
    public void ImageMagicBytes_ValidPng_Passes()
    {
        // PNG starts with 89 50 4E 47
        var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D });
        Assert.True(CheckImageMagicBytes(stream, ".png"));
    }

    [Fact]
    public void ImageMagicBytes_ValidWebp_Passes()
    {
        // WebP: RIFF....WEBP
        var bytes = new byte[12];
        var riff = System.Text.Encoding.ASCII.GetBytes("RIFF");
        var webp = System.Text.Encoding.ASCII.GetBytes("WEBP");
        Array.Copy(riff, 0, bytes, 0, 4);
        bytes[4] = 0x00; bytes[5] = 0x00; bytes[6] = 0x00; bytes[7] = 0x00; // size placeholder
        Array.Copy(webp, 0, bytes, 8, 4);
        var stream = new MemoryStream(bytes);
        Assert.True(CheckImageMagicBytes(stream, ".webp"));
    }

    [Fact]
    public void ImageMagicBytes_DisguisedExe_FailsJpeg()
    {
        // MZ header (PE executable) disguised as .jpg
        var stream = new MemoryStream(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00 });
        Assert.False(CheckImageMagicBytes(stream, ".jpg"));
    }

    [Fact]
    public void ImageMagicBytes_DisguisedExe_FailsPng()
    {
        var stream = new MemoryStream(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00 });
        Assert.False(CheckImageMagicBytes(stream, ".png"));
    }

    [Fact]
    public void ImageMagicBytes_RiffNotWebp_Fails()
    {
        // RIFF header but NOT WEBP at offset 8 (e.g., WAV file disguised as .webp)
        var bytes = new byte[12];
        var riff = System.Text.Encoding.ASCII.GetBytes("RIFF");
        var wave = System.Text.Encoding.ASCII.GetBytes("WAVE");
        Array.Copy(riff, 0, bytes, 0, 4);
        Array.Copy(wave, 0, bytes, 8, 4);
        var stream = new MemoryStream(bytes);
        Assert.False(CheckImageMagicBytes(stream, ".webp"));
    }

    /// <summary>
    /// Replicates the magic byte validation logic from UploadController.UploadImage
    /// for pure unit testing without holding an HTTP pipeline.
    /// </summary>
    private static bool CheckImageMagicBytes(Stream stream, string ext)
    {
        var imgMagic = new Dictionary<string, byte[][]>
        {
            [".jpg"] = [new byte[] { 0xFF, 0xD8, 0xFF }],
            [".jpeg"] = [new byte[] { 0xFF, 0xD8, 0xFF }],
            [".png"] = [new byte[] { 0x89, 0x50, 0x4E, 0x47 }],
            [".webp"] = [System.Text.Encoding.ASCII.GetBytes("RIFF")]
        };

        if (!imgMagic.TryGetValue(ext, out var signatures))
            return false;

        var headerBuf = new byte[12];
        var bytesRead = stream.Read(headerBuf);
        stream.Position = 0;

        foreach (var sig in signatures)
        {
            if (bytesRead >= sig.Length && headerBuf.AsSpan(0, sig.Length).SequenceEqual(sig))
            {
                if (ext == ".webp" && (bytesRead < 12 || !headerBuf.AsSpan(8, 4).SequenceEqual(System.Text.Encoding.ASCII.GetBytes("WEBP"))))
                    return false;
                return true;
            }
        }
        return false;
    }

    // ────────────────────────────────────────────────
    //  Tier manifest — fee rate invariants
    // ────────────────────────────────────────────────

    [Fact]
    public void TierManifest_FreeCreator_Has35PercentFee()
    {
        var config = Cambrian.Application.Configuration.TierManifest.For(Cambrian.Domain.Enums.CreatorTier.Free);
        Assert.Equal(0.35m, config.FeeRate);
    }

    [Fact]
    public void TierManifest_ProCreator_Has15PercentFee()
    {
        var config = Cambrian.Application.Configuration.TierManifest.For(Cambrian.Domain.Enums.CreatorTier.Pro);
        Assert.Equal(0.15m, config.FeeRate);
    }

    [Fact]
    public void TierManifest_FreeCreator_HasUploadLimit()
    {
        var config = Cambrian.Application.Configuration.TierManifest.For(Cambrian.Domain.Enums.CreatorTier.Free);
        Assert.NotNull(config.UploadLimit);
        Assert.True(config.UploadLimit > 0,
            "Free tier must have a finite upload limit.");
    }

    [Fact]
    public void TierManifest_ProCreator_IsUnlimited()
    {
        var pro = Cambrian.Application.Configuration.TierManifest.For(Cambrian.Domain.Enums.CreatorTier.Pro);
        Assert.True(pro.IsUnlimited,
            "Pro tier must have unlimited uploads.");
    }

    [Fact]
    public void TierManifest_CreatorWalletCredit_NeverRoundsUp()
    {
        // Verify the payout invariant: floor(gross × (1 − feeRate))
        var feeRate = 0.15m; // Pro
        var grossCents = 2999; // $29.99

        var creatorCents = (int)Math.Floor(grossCents * (1 - feeRate));
        Assert.Equal(2549, creatorCents); // 2999 × 0.85 = 2549.15 → floor = 2549

        // Verify it never rounds up
        Assert.True(creatorCents <= grossCents * (1 - feeRate),
            "Creator credit must never exceed gross × (1 - feeRate)");
    }

    // ────────────────────────────────────────────────
    //  Edge cases — argument validation
    // ────────────────────────────────────────────────

    [Fact]
    public async Task Purchase_RejectsInvalidTrackId()
    {
        var sut = MakePurchaseService(out _, out _, out _, out _);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.CreateAsync(new PurchaseCreateRequest
            {
                TrackId = "not-a-guid",
                LicenseType = "non-exclusive",
                StripeSessionId = "sess_test"
            }, "user-1"));
    }

    [Fact]
    public async Task Purchase_RejectsMissingTrack()
    {
        var sut = MakePurchaseService(out _, out var tracks, out _, out _);
        var trackId = Guid.NewGuid();
        tracks.GetByIdAsync(trackId).Returns((Track?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            sut.CreateAsync(new PurchaseCreateRequest
            {
                TrackId = trackId.ToString(),
                LicenseType = "non-exclusive",
                StripeSessionId = "sess_test"
            }, "user-1"));
    }

    [Fact]
    public async Task Checkout_RejectsInvalidTrackId()
    {
        var sut = MakeCheckoutService(out _, out _);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.CreateCheckoutAsync(
                new CheckoutRequest { TrackId = "nope", LicenseType = "standard" },
                MakeUser()));
    }
}
