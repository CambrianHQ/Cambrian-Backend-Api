using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text.Json;

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
            return StatusCode(400, "Invalid webhook signature.");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "EVENT: StripeWebhookFailed — invalid JSON payload");
            return StatusCode(400, "Invalid webhook payload.");
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogError(ex, "EVENT: StripeWebhookFailed — missing required payload fields");
            return StatusCode(400, "Invalid webhook payload.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("signature verification"))
        {
            _logger.LogError(ex, "EVENT: StripeWebhookFailed — signature verification failed");
            return StatusCode(400, "Webhook signature verification failed.");
        }
        catch (Exception ex)
        {
            // Return 500 so Stripe retries — the event is persisted as "failed"
            // in the database for investigation. Idempotency checks prevent
            // duplicate processing on successful retry.
            _logger.LogError(ex, "EVENT: StripeWebhookProcessingError — returning 500 for Stripe retry");
            return StatusCode(500, "Webhook processing failed.");
        }
    }
}
