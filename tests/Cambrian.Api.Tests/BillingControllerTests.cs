using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests for BillingController covering subscription checkout via Stripe,
/// tier validation, billing status, and session retrieval.
/// </summary>
public sealed class BillingControllerTests
{
    private readonly ISubscriptionRepository _subscriptions = Substitute.For<ISubscriptionRepository>();
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly BillingController _controller;

    public BillingControllerTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FrontendUrl"] = "https://app.test"
            })
            .Build();

        _controller = new BillingController(_subscriptions, _gateway, config);
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
        var result = await _controller.Checkout(new BillingCheckoutRequest { Tier = "enterprise" });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(bad.Value);
        Assert.Contains("Invalid tier", envelope.Error);
    }

    [Fact]
    public async Task Checkout_Returns400_WhenTierNull()
    {
        SetupUser();
        var result = await _controller.Checkout(new BillingCheckoutRequest { Tier = null });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Checkout_Returns400_WhenTierFree()
    {
        SetupUser();
        var result = await _controller.Checkout(new BillingCheckoutRequest { Tier = "free" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Checkout_CreatesStripeSession_ForPaidTier()
    {
        SetupUser("buyer-1");
        _gateway.CreateSubscriptionCheckoutAsync(
            499, "Paid Listener", "buyer-1:subscription:paid",
            "https://app.test/checkout/success?session_id={CHECKOUT_SESSION_ID}",
            "https://app.test/checkout/cancel")
            .Returns("https://stripe.test/session");

        var result = await _controller.Checkout(new BillingCheckoutRequest { Tier = "paid" });

        Assert.IsType<OkObjectResult>(result);
        await _gateway.Received(1).CreateSubscriptionCheckoutAsync(
            499, "Paid Listener", "buyer-1:subscription:paid",
            "https://app.test/checkout/success?session_id={CHECKOUT_SESSION_ID}",
            "https://app.test/checkout/cancel");
    }

    [Fact]
    public async Task Checkout_CreatesStripeSession_ForCreatorTier()
    {
        SetupUser("creator-1");
        _gateway.CreateSubscriptionCheckoutAsync(
            999, "Creator", "creator-1:subscription:creator",
            Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://stripe.test/session");

        var result = await _controller.Checkout(new BillingCheckoutRequest { Tier = "creator" });

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Checkout_IsCaseInsensitive()
    {
        SetupUser();
        _gateway.CreateSubscriptionCheckoutAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://stripe.test/session");

        var result = await _controller.Checkout(new BillingCheckoutRequest { Tier = "PAID" });

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Checkout_UsesLocalhostFallback_WhenFrontendUrlBlank()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FrontendUrl"] = " "
            })
            .Build();
        var controller = new BillingController(_subscriptions, _gateway, config);
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "buyer-2")
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        _gateway.CreateSubscriptionCheckoutAsync(
            499,
            "Paid Listener",
            "buyer-2:subscription:paid",
            "http://localhost:5173/checkout/success?session_id={CHECKOUT_SESSION_ID}",
            "http://localhost:5173/checkout/cancel")
            .Returns("https://stripe.test/session");

        var result = await controller.Checkout(new BillingCheckoutRequest { Tier = "paid" });

        Assert.IsType<OkObjectResult>(result);
        await _gateway.Received(1).CreateSubscriptionCheckoutAsync(
            499,
            "Paid Listener",
            "buyer-2:subscription:paid",
            "http://localhost:5173/checkout/success?session_id={CHECKOUT_SESSION_ID}",
            "http://localhost:5173/checkout/cancel");
    }

    // ── CheckoutSession (alias) ──

    [Fact]
    public async Task CheckoutSession_DelegatesToCheckout()
    {
        SetupUser();
        _gateway.CreateSubscriptionCheckoutAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://stripe.test/session");

        var result = await _controller.CheckoutSession(new BillingCheckoutRequest { Tier = "paid" });

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Status ──

    [Fact]
    public async Task Status_ReturnsFree_WhenNoSubscription()
    {
        SetupUser();
        _subscriptions.GetActiveAsync("user-1").Returns((Subscription?)null);

        var result = await _controller.Status();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Status_ReturnsActivePlan()
    {
        SetupUser();
        _subscriptions.GetActiveAsync("user-1").Returns(new Subscription
        {
            Id = Guid.NewGuid(),
            Plan = "creator",
            Status = "active",
            UserId = "user-1"
        });

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
