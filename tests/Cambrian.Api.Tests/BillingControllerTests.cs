using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    private readonly BillingController _controller;

    public BillingControllerTests()
    {
        _controller = new BillingController(_billing);
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
    public async Task Checkout_Returns400_WhenTierInvalid()
    {
        SetupUser();
        _billing.CreateCheckoutAsync(Arg.Any<BillingCheckoutRequest>(), Arg.Any<string>())
            .ThrowsAsync(new ArgumentException("Invalid tier. Choose 'paid' or 'creator'."));

        var result = await _controller.Checkout(new BillingCheckoutRequest { Tier = "enterprise" });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(bad.Value);
        Assert.Contains("Invalid tier", envelope.Error);
    }

    [Fact]
    public async Task Checkout_Returns400_WhenTierNull()
    {
        SetupUser();
        _billing.CreateCheckoutAsync(Arg.Any<BillingCheckoutRequest>(), Arg.Any<string>())
            .ThrowsAsync(new ArgumentException("Invalid tier. Choose 'paid' or 'creator'."));

        var result = await _controller.Checkout(new BillingCheckoutRequest { Tier = null });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Checkout_Returns400_WhenTierFree()
    {
        SetupUser();
        _billing.CreateCheckoutAsync(Arg.Any<BillingCheckoutRequest>(), Arg.Any<string>())
            .ThrowsAsync(new ArgumentException("Invalid tier. Choose 'paid' or 'creator'."));

        var result = await _controller.Checkout(new BillingCheckoutRequest { Tier = "free" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Checkout_CreatesStripeSession_ForPaidTier()
    {
        SetupUser("buyer-1");
        _billing.CreateCheckoutAsync(
            Arg.Is<BillingCheckoutRequest>(r => r.Tier == "paid"), "buyer-1")
            .Returns(new CheckoutResponse { CheckoutUrl = "https://stripe.test/session" });

        var result = await _controller.Checkout(new BillingCheckoutRequest { Tier = "paid" });

        Assert.IsType<OkObjectResult>(result);
        await _billing.Received(1).CreateCheckoutAsync(
            Arg.Is<BillingCheckoutRequest>(r => r.Tier == "paid"), "buyer-1");
    }

    [Fact]
    public async Task Checkout_CreatesStripeSession_ForCreatorTier()
    {
        SetupUser("creator-1");
        _billing.CreateCheckoutAsync(
            Arg.Is<BillingCheckoutRequest>(r => r.Tier == "creator"), "creator-1")
            .Returns(new CheckoutResponse { CheckoutUrl = "https://stripe.test/session" });

        var result = await _controller.Checkout(new BillingCheckoutRequest { Tier = "creator" });

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Checkout_IsCaseInsensitive()
    {
        SetupUser();
        _billing.CreateCheckoutAsync(Arg.Any<BillingCheckoutRequest>(), Arg.Any<string>())
            .Returns(new CheckoutResponse { CheckoutUrl = "https://stripe.test/session" });

        var result = await _controller.Checkout(new BillingCheckoutRequest { Tier = "PAID" });

        Assert.IsType<OkObjectResult>(result);
    }

    // ── CheckoutSession (alias) ──

    [Fact]
    public async Task CheckoutSession_DelegatesToCheckout()
    {
        SetupUser();
        _billing.CreateCheckoutAsync(Arg.Any<BillingCheckoutRequest>(), Arg.Any<string>())
            .Returns(new CheckoutResponse { CheckoutUrl = "https://stripe.test/session" });

        var result = await _controller.CheckoutSession(new BillingCheckoutRequest { Tier = "paid" });

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Status ──

    [Fact]
    public async Task Status_ReturnsFree_WhenNoSubscription()
    {
        SetupUser();
        _billing.GetStatusAsync("user-1")
            .Returns(new BillingStatusResponse { Tier = "free", Status = "active" });

        var result = await _controller.Status();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Status_ReturnsActivePlan()
    {
        SetupUser();
        _billing.GetStatusAsync("user-1")
            .Returns(new BillingStatusResponse { Tier = "creator", Status = "active" });

        var result = await _controller.Status();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── GetSession ──

    [Fact]
    public void GetSession_Returns400_WhenSessionIdEmpty()
    {
        SetupUser();
        var result = _controller.GetSession("");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetSession_ReturnsOk_WithSessionId()
    {
        SetupUser();
        var result = _controller.GetSession("cs_test_123");

        Assert.IsType<OkObjectResult>(result);
    }
}
