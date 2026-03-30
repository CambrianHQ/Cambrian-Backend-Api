using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests for BillingController covering subscription checkout via Stripe,
/// tier validation, billing status, and session retrieval.
/// </summary>
public sealed class BillingControllerTests
{
    private readonly IBillingService _billing = Substitute.For<IBillingService>();
    private readonly ILogger<BillingController> _logger = Substitute.For<ILogger<BillingController>>();
    private readonly BillingController _controller;

    public BillingControllerTests()
    {
        _controller = new BillingController(_billing, _logger);
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
    public async Task Checkout_Returns400_WhenServiceThrowsArgumentException()
    {
        SetupUser();
        _billing.CreateCheckoutAsync(Arg.Any<BillingCheckoutRequest>(), Arg.Any<string>(), Arg.Any<string?>())
            .ThrowsAsync(new ArgumentException("Invalid tier: enterprise"));

        var result = await _controller.Checkout(new BillingCheckoutRequest { Tier = "enterprise" });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(bad.Value);
        Assert.Contains("Invalid tier", envelope.Error);
    }

    [Fact]
    public async Task Checkout_ReturnsOk_WithCheckoutUrl()
    {
        SetupUser("buyer-1");
        _billing.CreateCheckoutAsync(Arg.Any<BillingCheckoutRequest>(), "buyer-1", Arg.Any<string?>())
            .Returns(new CheckoutResponse { CheckoutUrl = "https://stripe.test/session" });

        var result = await _controller.Checkout(new BillingCheckoutRequest { Tier = "paid" });

        Assert.IsType<OkObjectResult>(result);
    }

    // ── CheckoutSession (alias) ──

    [Fact]
    public async Task CheckoutSession_DelegatesToCheckout()
    {
        SetupUser();
        _billing.CreateCheckoutAsync(Arg.Any<BillingCheckoutRequest>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new CheckoutResponse { CheckoutUrl = "https://stripe.test/session" });

        var result = await _controller.CheckoutSession(new BillingCheckoutRequest { Tier = "paid" });

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Status ──

    [Fact]
    public async Task Status_ReturnsOk_WithBillingStatus()
    {
        SetupUser();
        _billing.GetStatusAsync("user-1").Returns(new BillingStatusResponse
        {
            Tier = "free",
            Status = "active"
        });

        var result = await _controller.Status();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── GetSession ──

    [Fact]
    public async Task GetSession_Returns400_WhenSessionIdEmpty()
    {
        SetupUser();
        var result = await _controller.GetSession("");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetSession_ReturnsOk_WithSessionStatus()
    {
        SetupUser();
        _billing.ConfirmCheckoutAsync("cs_test_123", "user-1")
            .Returns(new CheckoutSessionStatusResponse { Status = "paid", SessionId = "cs_test_123" });

        var result = await _controller.GetSession("cs_test_123");

        Assert.IsType<OkObjectResult>(result);
    }
}
