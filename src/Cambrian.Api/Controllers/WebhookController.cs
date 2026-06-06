using Cambrian.Application.DTOs.Email;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Sentry;
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
    [HttpPost("/api/stripe/webhook")]
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
            // Capture to Sentry: a webhook handler that throws here (e.g. a bug
            // that prevents granting Pro after a successful payment) must alert
            // us proactively rather than fail silently until a customer complains.
            // Return 500 so Stripe retries — the event is persisted as "failed"
            // in the database for investigation. Idempotency checks prevent
            // duplicate processing on successful retry.
            SentrySdk.CaptureException(ex);
            _logger.LogError(ex, "EVENT: StripeWebhookProcessingError — returning 500 for Stripe retry");
            return StatusCode(500, "Webhook processing failed.");
        }
    }

    [HttpPost("email")]
    public async Task<IActionResult> Email()
    {
        ResendWebhookEvent? evt;

        try
        {
            evt = await System.Text.Json.JsonSerializer.DeserializeAsync<ResendWebhookEvent>(
                Request.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "EVENT: ResendWebhookFailed — invalid JSON payload");
            return StatusCode(400, "Invalid webhook payload.");
        }

        if (evt is null)
            return StatusCode(400, "Empty webhook payload.");

        _logger.LogInformation(
            "EVENT: ResendWebhookReceived type:{Type} emailId:{EmailId}",
            evt.Type,
            evt.Data?.EmailId);

        if (evt.Type == "email.received")
        {
            // TODO: handle inbound email — parse evt.Data for from/subject/attachments
            _logger.LogInformation(
                "EVENT: ResendEmailReceived from:{From} subject:{Subject} attachments:{Count}",
                evt.Data?.From,
                evt.Data?.Subject,
                evt.Data?.Attachments.Count ?? 0);
        }

        return Ok(new { });
    }
}
