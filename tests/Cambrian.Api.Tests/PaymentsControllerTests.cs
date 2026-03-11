using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Payments;
using Cambrian.Application.DTOs.Purchases;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

/// <summary>
/// Controller-level tests for PaymentsController covering checkout,
/// state retrieval, payment result, payment processing, purchase creation,
/// and creator crediting. Validates HTTP status codes and correct delegation
/// to underlying services.
/// </summary>
public sealed class PaymentsControllerTests
{
    private readonly IPaymentService _payments = Substitute.For<IPaymentService>();
    private readonly IPurchaseService _purchaseService = Substitute.For<IPurchaseService>();
    private readonly PaymentsController _controller;

    public PaymentsControllerTests()
    {
        _controller = new PaymentsController(_payments, _purchaseService);
    }

    private void SetupUser(string userId = "user-1")
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId)
        }, "Test"));
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    // ── Checkout ──

    [Fact]
    public async Task Checkout_ReturnsOk_WithCheckoutUrl()
    {
        SetupUser();
        _payments.CreateCheckoutAsync(Arg.Any<PaymentCheckoutRequest>())
            .Returns(new PaymentCheckoutResponse { CheckoutUrl = "https://stripe.test/session" });

        var result = await _controller.Checkout(
            new PaymentCheckoutRequest { TrackId = Guid.NewGuid().ToString() });

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Checkout_PropagatesArgumentException_ForEmptyTrackId()
    {
        SetupUser();
        _payments.CreateCheckoutAsync(Arg.Any<PaymentCheckoutRequest>())
            .ThrowsAsync(new ArgumentException("TrackId is required."));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _controller.Checkout(new PaymentCheckoutRequest { TrackId = "" }));
    }

    // ── State ──

    [Fact]
    public async Task State_ReturnsOk()
    {
        SetupUser();
        _payments.GetStateAsync().Returns(new PaymentStateResponse { Status = "ready" });

        var result = await _controller.State();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Result (AllowAnonymous) ──

    [Fact]
    public async Task Result_ReturnsOk_WithStatus()
    {
        _payments.GetResultAsync("success", null).Returns(new PaymentResultResponse
        {
            Status = "success"
        });

        var result = await _controller.Result("success", null);

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Process ──

    [Fact]
    public async Task Process_ReturnsOk_WhenPaymentProcessed()
    {
        SetupUser();
        _payments.ProcessAsync(Arg.Any<PaymentProcessRequest>(), Arg.Any<string>()).Returns(Task.CompletedTask);

        var result = await _controller.Process(new PaymentProcessRequest
        {
            PurchaseId = Guid.NewGuid().ToString(),
            PaymentMethodId = "pm_test"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(ok.Value);
        Assert.Contains("processed", envelope.Message);
    }

    [Fact]
    public async Task Process_PropagatesKeyNotFoundException()
    {
        SetupUser();
        _payments.ProcessAsync(Arg.Any<PaymentProcessRequest>(), Arg.Any<string>())
            .ThrowsAsync(new KeyNotFoundException("Purchase not found."));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _controller.Process(new PaymentProcessRequest
            {
                PurchaseId = Guid.NewGuid().ToString()
            }));
    }

    // ── CreatePurchase ──

    [Fact]
    public async Task CreatePurchase_Returns201_WithPurchaseResponse()
    {
        SetupUser("buyer-1");
        var trackId = Guid.NewGuid();
        _purchaseService.CreateAsync(Arg.Any<PurchaseCreateRequest>(), "buyer-1")
            .Returns(new PurchaseResponse
            {
                Id = Guid.NewGuid().ToString(),
                TrackId = trackId.ToString(),
                TrackTitle = "Beat",
                AmountCents = 2999,
                LicenseType = "non-exclusive",
                Status = "completed"
            });

        var result = await _controller.CreatePurchase(new PurchaseCreateRequest
        {
            TrackId = trackId.ToString(),
            LicenseType = "non-exclusive"
        });

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, obj.StatusCode);
    }

    [Fact]
    public async Task CreatePurchase_PropagatesInvalidOperation_ForDuplicate()
    {
        SetupUser("buyer-1");
        _purchaseService.CreateAsync(Arg.Any<PurchaseCreateRequest>(), "buyer-1")
            .ThrowsAsync(new InvalidOperationException("You already own this track."));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _controller.CreatePurchase(new PurchaseCreateRequest
            {
                TrackId = Guid.NewGuid().ToString()
            }));
    }

    // ── CreditCreator ──

    [Fact]
    public async Task CreditCreator_ReturnsOk()
    {
        SetupUser();
        _purchaseService.CreditCreatorAsync(Arg.Any<CreditCreatorRequest>())
            .Returns(Task.CompletedTask);

        var result = await _controller.CreditCreator(new CreditCreatorRequest());

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(ok.Value);
        Assert.Contains("credited", envelope.Message);
    }
}
