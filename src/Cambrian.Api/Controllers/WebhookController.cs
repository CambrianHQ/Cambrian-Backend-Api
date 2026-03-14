using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("webhook")]
public class WebhookController : BaseController
{
    private readonly IWebhookService _webhooks;
    private readonly ILogger<WebhookController> _logger;

    public WebhookController(IWebhookService webhooks, ILogger<WebhookController> logger)
    {
        _webhooks = webhooks;
        _logger = logger;
    }

    [HttpPost("stripe")]
    public async Task<IActionResult> Stripe()
    {
        var signature = Request.Headers["Stripe-Signature"].FirstOrDefault();

        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();

        _logger.LogInformation("EVENT: StripeWebhookReceived payloadLength:{Length} signaturePresent:{SigPresent}", json.Length, !string.IsNullOrEmpty(signature));

        try
        {
            await _webhooks.HandleStripeAsync(json, signature ?? "");
            _logger.LogInformation("EVENT: StripeWebhookProcessed");
            return MessageResponse("Received.");
        }
        catch (Stripe.StripeException ex)
        {
            _logger.LogError(ex, "EVENT: StripeWebhookFailed — invalid signature");
            return ErrorResponse("Invalid webhook signature.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("signature verification"))
        {
            _logger.LogError(ex, "EVENT: StripeWebhookFailed — signature verification failed");
            return ErrorResponse("Webhook signature verification failed.");
        }
    }
}