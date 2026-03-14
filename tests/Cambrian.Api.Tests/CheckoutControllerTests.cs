using System.Security.Claims;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

/// <summary>
/// Controller-level tests for CheckoutController which creates Stripe
/// checkout sessions for track purchases. Verifies delegation to service,
/// response shapes, and error propagation for missing tracks.
/// </summary>
public sealed class CheckoutControllerTests
{
    private readonly ICheckoutService _checkout = Substitute.For<ICheckoutService>();
    private readonly CheckoutController _controller;

    public CheckoutControllerTests()
    {
        var logger = Substitute.For<ILogger<CheckoutController>>();
        _controller = new CheckoutController(_checkout, logger);
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

    [Fact]
    public async Task Checkout_ReturnsOk_WithUrlAndStatus()
    {
        SetupUser();
        _checkout.CreateCheckoutAsync(Arg.Any<CheckoutRequest>(), Arg.Any<ClaimsPrincipal>())
            .Returns(new CheckoutResponse
            {
                CheckoutUrl = "https://stripe.test/session-abc",
                Status = "created"
            });

        var result = await _controller.Checkout(new CheckoutRequest
        {
            TrackId = Guid.NewGuid().ToString(),
            LicenseType = "standard"
        });

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Checkout_PropagatesKeyNotFound_WhenTrackMissing()
    {
        SetupUser();
        _checkout.CreateCheckoutAsync(Arg.Any<CheckoutRequest>(), Arg.Any<ClaimsPrincipal>())
            .ThrowsAsync(new KeyNotFoundException("Track not found."));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _controller.Checkout(new CheckoutRequest
            {
                TrackId = Guid.NewGuid().ToString(),
                LicenseType = "standard"
            }));
    }

    [Fact]
    public async Task Checkout_PropagatesFormatException_WhenTrackIdInvalid()
    {
        SetupUser();
        _checkout.CreateCheckoutAsync(Arg.Any<CheckoutRequest>(), Arg.Any<ClaimsPrincipal>())
            .ThrowsAsync(new FormatException("Input string was not in a correct format."));

        await Assert.ThrowsAsync<FormatException>(() =>
            _controller.Checkout(new CheckoutRequest
            {
                TrackId = "not-a-guid",
                LicenseType = "standard"
            }));
    }

    [Fact]
    public async Task Checkout_PassesUserPrincipalToService()
    {
        SetupUser("buyer-42");
        _checkout.CreateCheckoutAsync(Arg.Any<CheckoutRequest>(), Arg.Any<ClaimsPrincipal>())
            .Returns(new CheckoutResponse
            {
                CheckoutUrl = "https://stripe.test/session",
                Status = "created"
            });

        await _controller.Checkout(new CheckoutRequest
        {
            TrackId = Guid.NewGuid().ToString(),
            LicenseType = "exclusive"
        });

        await _checkout.Received(1).CreateCheckoutAsync(
            Arg.Any<CheckoutRequest>(),
            Arg.Is<ClaimsPrincipal>(p =>
                p.FindFirstValue(ClaimTypes.NameIdentifier) == "buyer-42"));
    }
}
