using System.Security.Claims;
using Cambrian.Api.Controllers.v1;
using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.DTOs.Licenses;
using Cambrian.Application.DTOs.V1;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Interfaces.V1;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests.V1;

/// <summary>
/// Contract tests for the public /api/v1/licenses endpoints. Covers the
/// canonical envelope shape, the idempotency replay contract, and the
/// authorization boundary. Full HTTP-pipeline tests (rate limits, API-key
/// middleware) are intentionally out of scope here — those behaviors belong
/// to middleware tests. See ApiKeyMiddleware tests for 401 coverage.
/// </summary>
[Trait("Category", "Critical")]
public sealed class LicensesV1ControllerTests
{
    private const string TestUserId = "user-1";
    private const string RouteKey = "POST /api/v1/licenses/purchase";

    private readonly ICheckoutService _checkout = Substitute.For<ICheckoutService>();
    private readonly ILicenseService _licenses = Substitute.For<ILicenseService>();
    private readonly IIdempotencyStore _idempotency = Substitute.For<IIdempotencyStore>();
    private readonly ILogger<LicensesV1Controller> _logger = Substitute.For<ILogger<LicensesV1Controller>>();
    private readonly LicensesV1Controller _sut;

    public LicensesV1ControllerTests()
    {
        _sut = new LicensesV1Controller(_checkout, _licenses, _idempotency, _logger);
        _sut.ControllerContext = new ControllerContext { HttpContext = AuthedContext(TestUserId) };
    }

    private static DefaultHttpContext AuthedContext(string userId)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Email, "u@test.com"),
        }, "Test"));
        return ctx;
    }

    private static DefaultHttpContext AnonymousContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
        return ctx;
    }

    // ── Purchase: envelope & happy path ──

    [Fact]
    public async Task Purchase_WithoutAuth_Returns401WithEnvelope()
    {
        _sut.ControllerContext = new ControllerContext { HttpContext = AnonymousContext() };
        var request = new LicensePurchaseRequest { TrackId = Guid.NewGuid().ToString(), LicenseType = "non-exclusive" };

        var result = await _sut.Purchase(request, idempotencyKey: null, CancellationToken.None);

        var unauth = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        var env = Assert.IsType<V1ApiResponse<LicensePurchaseResponse>>(unauth.Value);
        Assert.False(env.Success);
        Assert.NotNull(env.Error);
        Assert.Null(env.Data);
    }

    [Fact]
    public async Task Purchase_WithoutIdempotencyKey_CallsStripeAndReturnsEnvelope()
    {
        _checkout.CreateCheckoutAsync(Arg.Any<CheckoutRequest>(), Arg.Any<ClaimsPrincipal>())
            .Returns(new CheckoutResponse { CheckoutUrl = "https://checkout.stripe.com/x", Status = "created" });

        var request = new LicensePurchaseRequest { TrackId = Guid.NewGuid().ToString(), LicenseType = "non-exclusive" };

        var result = await _sut.Purchase(request, idempotencyKey: null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var env = Assert.IsType<V1ApiResponse<LicensePurchaseResponse>>(ok.Value);
        Assert.True(env.Success);
        Assert.NotNull(env.Data);
        Assert.Equal("https://checkout.stripe.com/x", env.Data!.CheckoutUrl);
        Assert.False(env.Data.Idempotent);
        await _checkout.Received(1).CreateCheckoutAsync(Arg.Any<CheckoutRequest>(), Arg.Any<ClaimsPrincipal>());
        // No idempotency key → nothing stored.
        await _idempotency.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(),
            Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    // ── Idempotency contract ──

    [Fact]
    public async Task Purchase_WithIdempotencyKey_FirstCall_SavesToStore()
    {
        _checkout.CreateCheckoutAsync(Arg.Any<CheckoutRequest>(), Arg.Any<ClaimsPrincipal>())
            .Returns(new CheckoutResponse { CheckoutUrl = "https://checkout.stripe.com/first", Status = "created" });
        _idempotency.TryGetAsync("key-abc", TestUserId, RouteKey, Arg.Any<CancellationToken>())
            .Returns((IdempotentResponse?)null);

        var request = new LicensePurchaseRequest { TrackId = Guid.NewGuid().ToString(), LicenseType = "non-exclusive" };

        var result = await _sut.Purchase(request, idempotencyKey: "key-abc", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var env = Assert.IsType<V1ApiResponse<LicensePurchaseResponse>>(ok.Value);
        Assert.True(env.Success);
        Assert.False(env.Data!.Idempotent);
        await _idempotency.Received(1).SaveAsync(
            "key-abc", TestUserId, RouteKey, StatusCodes.Status200OK,
            Arg.Is<string>(b => b.Contains("https://checkout.stripe.com/first")),
            Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purchase_WithIdempotencyKey_ReplayReturnsCachedWithIdempotentFlag_NoStripeCall()
    {
        var cachedJson = System.Text.Json.JsonSerializer.Serialize(new LicensePurchaseResponse
        {
            CheckoutUrl = "https://checkout.stripe.com/original",
            Status = "created",
            Idempotent = false,
        });
        _idempotency.TryGetAsync("key-abc", TestUserId, RouteKey, Arg.Any<CancellationToken>())
            .Returns(new IdempotentResponse(StatusCodes.Status200OK, cachedJson));

        var request = new LicensePurchaseRequest { TrackId = Guid.NewGuid().ToString(), LicenseType = "non-exclusive" };

        var result = await _sut.Purchase(request, idempotencyKey: "key-abc", CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status200OK, obj.StatusCode);
        var env = Assert.IsType<V1ApiResponse<LicensePurchaseResponse>>(obj.Value);
        Assert.True(env.Success);
        Assert.True(env.Data!.Idempotent);
        Assert.Equal("https://checkout.stripe.com/original", env.Data.CheckoutUrl);
        // Crucially: Stripe was NOT called on the replay.
        await _checkout.DidNotReceive().CreateCheckoutAsync(Arg.Any<CheckoutRequest>(), Arg.Any<ClaimsPrincipal>());
    }

    [Fact]
    public async Task Purchase_DifferentIdempotencyKey_MakesFreshStripeCall()
    {
        _idempotency.TryGetAsync("different-key", TestUserId, RouteKey, Arg.Any<CancellationToken>())
            .Returns((IdempotentResponse?)null);
        _checkout.CreateCheckoutAsync(Arg.Any<CheckoutRequest>(), Arg.Any<ClaimsPrincipal>())
            .Returns(new CheckoutResponse { CheckoutUrl = "https://checkout.stripe.com/different", Status = "created" });

        var request = new LicensePurchaseRequest { TrackId = Guid.NewGuid().ToString(), LicenseType = "non-exclusive" };

        var result = await _sut.Purchase(request, idempotencyKey: "different-key", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var env = Assert.IsType<V1ApiResponse<LicensePurchaseResponse>>(ok.Value);
        Assert.Equal("https://checkout.stripe.com/different", env.Data!.CheckoutUrl);
        await _checkout.Received(1).CreateCheckoutAsync(Arg.Any<CheckoutRequest>(), Arg.Any<ClaimsPrincipal>());
    }

    [Fact]
    public async Task Purchase_StripeThrowsInvalidOperation_MapsTo400()
    {
        _checkout.CreateCheckoutAsync(Arg.Any<CheckoutRequest>(), Arg.Any<ClaimsPrincipal>())
            .Returns<CheckoutResponse>(_ => throw new InvalidOperationException("You already own this license."));

        var request = new LicensePurchaseRequest { TrackId = Guid.NewGuid().ToString(), LicenseType = "non-exclusive" };

        var result = await _sut.Purchase(request, idempotencyKey: null, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var env = Assert.IsType<V1ApiResponse<LicensePurchaseResponse>>(bad.Value);
        Assert.False(env.Success);
        Assert.Contains("already own", env.Error);
    }

    [Fact]
    public async Task Purchase_StripeFailureIsNotCached()
    {
        _idempotency.TryGetAsync("key-xyz", TestUserId, RouteKey, Arg.Any<CancellationToken>())
            .Returns((IdempotentResponse?)null);
        _checkout.CreateCheckoutAsync(Arg.Any<CheckoutRequest>(), Arg.Any<ClaimsPrincipal>())
            .Returns<CheckoutResponse>(_ => throw new InvalidOperationException("track not found"));

        var request = new LicensePurchaseRequest { TrackId = Guid.NewGuid().ToString(), LicenseType = "non-exclusive" };

        await _sut.Purchase(request, idempotencyKey: "key-xyz", CancellationToken.None);

        // A failed purchase must NOT poison the idempotency cache — the client
        // should be able to retry with the same key once the underlying issue
        // is fixed (e.g. bad trackId, malformed payload).
        await _idempotency.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    // ── Verify ──

    [Fact]
    public async Task VerifyLicense_ValidId_ReturnsEnvelopeWithValidTrue()
    {
        _licenses.GetByIdAsync("lic-1").Returns(new LicenseCertificateDto
        {
            LicenseId = "lic-1",
            TrackId = "CAMB-TRK-ABCDEFGH",
            LicenseType = "non-exclusive",
            UsageType = "personal",
            BuyerId = "buyer-1",
            CreatorId = "creator-1",
            IssuedAt = DateTime.UtcNow,
        });

        var result = await _sut.VerifyLicense("lic-1");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var env = Assert.IsType<V1ApiResponse<LicenseVerifyResponse>>(ok.Value);
        Assert.True(env.Success);
        Assert.NotNull(env.Data);
        Assert.True(env.Data!.Valid);
        Assert.Equal("lic-1", env.Data.LicenseId);
    }

    [Fact]
    public async Task VerifyLicense_MissingId_Returns404Envelope()
    {
        _licenses.GetByIdAsync("missing").Returns((LicenseCertificateDto?)null);

        var result = await _sut.VerifyLicense("missing");

        var nf = Assert.IsType<NotFoundObjectResult>(result.Result);
        var env = Assert.IsType<V1ApiResponse<LicenseVerifyResponse>>(nf.Value);
        Assert.False(env.Success);
        Assert.Null(env.Data);
        Assert.Equal("License not found.", env.Error);
    }
}
