using System.Text;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

public sealed class WebhookControllerTests
{
    private readonly IWebhookService _webhookService = Substitute.For<IWebhookService>();
    private readonly WebhookController _controller;

    public WebhookControllerTests()
    {
        _controller = new WebhookController(_webhookService);
    }

    private void SetupRequest(string body, string? stripeSignature)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (stripeSignature is not null)
            context.Request.Headers["Stripe-Signature"] = stripeSignature;
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    [Fact]
    public async Task Stripe_ReturnsOk_WhenServiceSucceeds()
    {
        SetupRequest("{}", "sig_test");
        _webhookService.HandleStripeAsync("{}", "sig_test").Returns(Task.CompletedTask);

        var result = await _controller.Stripe();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(okResult.Value);
        Assert.True(envelope.Success);
        Assert.Equal("Received.", envelope.Message);
    }

    [Fact]
    public async Task Stripe_ReturnsBadRequest_WhenStripeExceptionThrown()
    {
        SetupRequest("{}", "bad_sig");
        _webhookService.HandleStripeAsync(Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new Stripe.StripeException("Invalid signature"));

        var result = await _controller.Stripe();

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(badRequest.Value);
        Assert.False(envelope.Success);
        Assert.Equal("Invalid webhook signature.", envelope.Error);
    }

    [Fact]
    public async Task Stripe_ReturnsBadRequest_WhenSignatureVerificationFails()
    {
        SetupRequest("{}", "");
        _webhookService.HandleStripeAsync(Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException(
                "Stripe webhook signature verification failed. Ensure Stripe:WebhookSecret is configured."));

        var result = await _controller.Stripe();

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(badRequest.Value);
        Assert.False(envelope.Success);
        Assert.Equal("Webhook signature verification failed.", envelope.Error);
    }

    [Fact]
    public async Task Stripe_PassesEmptyString_WhenSignatureHeaderMissing()
    {
        SetupRequest("{\"type\":\"test\"}", null);
        _webhookService.HandleStripeAsync(Arg.Any<string>(), "").Returns(Task.CompletedTask);

        var result = await _controller.Stripe();

        Assert.IsType<OkObjectResult>(result);
        await _webhookService.Received(1).HandleStripeAsync(Arg.Any<string>(), "");
    }
}
