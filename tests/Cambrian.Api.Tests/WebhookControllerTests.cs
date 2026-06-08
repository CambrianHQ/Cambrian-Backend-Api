using System.Security.Cryptography;
using System.Text;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

public sealed class WebhookControllerTests
{
    private readonly IWebhookService _webhookService = Substitute.For<IWebhookService>();
    private readonly WebhookController _controller;
    private readonly WebhookController _controllerWithSecret;

    // A stable test signing key (base64 of 32 random bytes).
    private const string TestWebhookSecretBase64 = "dGVzdC1zZWNyZXQtMzItYnl0ZXMtZm9yLXVuaXQtdGVzdA==";
    private const string TestWebhookSecret = "whsec_" + TestWebhookSecretBase64;

    public WebhookControllerTests()
    {
        var logger = Substitute.For<ILogger<WebhookController>>();
        var noSecret = Options.Create(new EmailOptions());
        _controller = new WebhookController(_webhookService, logger, noSecret);

        var withSecret = Options.Create(new EmailOptions { ResendWebhookSecret = TestWebhookSecret });
        _controllerWithSecret = new WebhookController(_webhookService, logger, withSecret);
    }

    private void SetupRequest(string body, string? stripeSignature, WebhookController? target = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (stripeSignature is not null)
            context.Request.Headers["Stripe-Signature"] = stripeSignature;
        (target ?? _controller).ControllerContext = new ControllerContext { HttpContext = context };
    }

    private void SetupEmailRequest(
        string body,
        string? svixId = null,
        string? svixTimestamp = null,
        string? svixSignature = null,
        WebhookController? target = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (svixId        is not null) context.Request.Headers["svix-id"]        = svixId;
        if (svixTimestamp is not null) context.Request.Headers["svix-timestamp"] = svixTimestamp;
        if (svixSignature is not null) context.Request.Headers["svix-signature"] = svixSignature;
        (target ?? _controller).ControllerContext = new ControllerContext { HttpContext = context };
    }

    private static string ComputeSvixSignature(string svixId, string svixTimestamp, string body)
    {
        var keyBytes      = Convert.FromBase64String(TestWebhookSecretBase64);
        var signedContent = Encoding.UTF8.GetBytes($"{svixId}.{svixTimestamp}.{body}");
        var hmac          = HMACSHA256.HashData(keyBytes, signedContent);
        return "v1," + Convert.ToBase64String(hmac);
    }

    // ── Stripe ────────────────────────────────────────────────────────────────

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

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.Equal("Invalid webhook signature.", objectResult.Value);
    }

    [Fact]
    public async Task Stripe_ReturnsBadRequest_WhenSignatureVerificationFails()
    {
        SetupRequest("{}", "");
        _webhookService.HandleStripeAsync(Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException(
                "Stripe webhook signature verification failed. Ensure Stripe:WebhookSecret is configured."));

        var result = await _controller.Stripe();

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.Equal("Webhook signature verification failed.", objectResult.Value);
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

    [Fact]
    public async Task Stripe_Returns500_WhenProcessingErrorOccurs()
    {
        SetupRequest("{}", "sig_test");
        _webhookService.HandleStripeAsync(Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new Exception("Processing failed"));

        var result = await _controller.Stripe();

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
        Assert.Equal("Webhook processing failed.", objectResult.Value);
    }

    // ── Resend email webhook signature verification ────────────────────────

    [Fact]
    public async Task Email_ReturnsOk_WhenNoSecretConfigured()
    {
        // Without a webhook secret configured the endpoint accepts any payload (backwards-compat).
        var body = "{\"type\":\"email.delivered\",\"data\":{\"email_id\":\"abc\"}}";
        SetupEmailRequest(body);

        var result = await _controller.Email();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Email_ReturnsOk_WhenValidSignature()
    {
        var body      = "{\"type\":\"email.delivered\",\"data\":{\"email_id\":\"abc\"}}";
        var svixId    = "msg_01j";
        var svixTs    = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var svixSig   = ComputeSvixSignature(svixId, svixTs, body);

        SetupEmailRequest(body, svixId, svixTs, svixSig, _controllerWithSecret);

        var result = await _controllerWithSecret.Email();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Email_Returns400_WhenSignatureMismatch()
    {
        var body    = "{\"type\":\"email.delivered\",\"data\":{\"email_id\":\"abc\"}}";
        var svixId  = "msg_01j";
        var svixTs  = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        SetupEmailRequest(body, svixId, svixTs, "v1,invalidsignature", _controllerWithSecret);

        var result = await _controllerWithSecret.Email();

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.Equal("Invalid webhook signature.", objectResult.Value);
    }

    [Fact]
    public async Task Email_Returns400_WhenSvixHeadersMissing()
    {
        var body = "{\"type\":\"email.delivered\",\"data\":{\"email_id\":\"abc\"}}";
        // No svix headers — should reject when secret is configured.
        SetupEmailRequest(body, target: _controllerWithSecret);

        var result = await _controllerWithSecret.Email();

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.Equal("Missing webhook signature headers.", objectResult.Value);
    }

    [Fact]
    public async Task Email_Returns400_WhenTimestampTooOld()
    {
        var body    = "{\"type\":\"email.delivered\",\"data\":{\"email_id\":\"abc\"}}";
        var svixId  = "msg_old";
        // 10 minutes in the past — beyond the 5-minute tolerance.
        var svixTs  = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600).ToString();
        var svixSig = ComputeSvixSignature(svixId, svixTs, body);

        SetupEmailRequest(body, svixId, svixTs, svixSig, _controllerWithSecret);

        var result = await _controllerWithSecret.Email();

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.Equal("Webhook timestamp out of tolerance.", objectResult.Value);
    }
}
