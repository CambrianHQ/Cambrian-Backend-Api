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
        var signature = Request.Headers["Stripe-Signature"].ToString();

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
            // Return 400 for signature failures — Stripe should not retry these
            return StatusCode(400, "Invalid webhook signature.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("signature verification"))
        {
            _logger.LogError(ex, "EVENT: StripeWebhookFailed — signature verification failed");
            return StatusCode(400, "Webhook signature verification failed.");
        }
        catch (Exception ex)
        {
            // Return 200 OK even on processing errors to prevent Stripe retry storms.
            // The error is logged for investigation but Stripe won't keep retrying for 72h.
            _logger.LogError(ex, "EVENT: StripeWebhookProcessingError — returning 200 to stop retries");
            return MessageResponse("Received (processing error logged).");
        }
    }
}