using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cambrian.Application.DTOs.Email;
using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sentry;

namespace Cambrian.Api.Controllers;

[Route("webhook")]
public class WebhookController : BaseController
{
    private readonly IWebhookService _webhooks;
    private readonly ILogger<WebhookController> _logger;
    private readonly EmailOptions _emailOptions;

    public WebhookController(IWebhookService webhooks, ILogger<WebhookController> logger, IOptions<EmailOptions> emailOptions)
    {
        _webhooks = webhooks;
        _logger = logger;
        _emailOptions = emailOptions.Value;
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
        // Buffer the body so we can (a) verify the signature and (b) deserialize.
        using var bodyReader = new StreamReader(Request.Body);
        var rawBody = await bodyReader.ReadToEndAsync();

        // Verify Resend's Svix webhook signature when a secret is configured.
        // Resend signs payloads using HMAC-SHA256 per the Svix spec:
        //   signed_content = "{svix-id}.{svix-timestamp}.{body}"
        //   signature      = base64(HMAC-SHA256(base64url-decoded(secret[6:]), signed_content))
        //   header value   = "v1,{signature}"
        var webhookSecret = _emailOptions.ResendWebhookSecret;
        if (!string.IsNullOrWhiteSpace(webhookSecret))
        {
            var svixId        = Request.Headers["svix-id"].ToString();
            var svixTimestamp = Request.Headers["svix-timestamp"].ToString();
            var svixSignature = Request.Headers["svix-signature"].ToString();

            if (string.IsNullOrEmpty(svixId) || string.IsNullOrEmpty(svixTimestamp) || string.IsNullOrEmpty(svixSignature))
            {
                _logger.LogWarning("EVENT: ResendWebhookFailed — missing Svix signature headers");
                return StatusCode(400, "Missing webhook signature headers.");
            }

            // Replay protection: reject events older than 5 minutes.
            if (!long.TryParse(svixTimestamp, out var tsSeconds)
                || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - tsSeconds) > 300)
            {
                _logger.LogWarning("EVENT: ResendWebhookFailed — timestamp out of tolerance svix-timestamp:{Ts}", svixTimestamp);
                return StatusCode(400, "Webhook timestamp out of tolerance.");
            }

            // The Svix secret is prefixed with "whsec_" and base64-encoded after that.
            var secretBase64 = webhookSecret.StartsWith("whsec_", StringComparison.Ordinal)
                ? webhookSecret[6..]
                : webhookSecret;

            byte[] keyBytes;
            try { keyBytes = Convert.FromBase64String(secretBase64); }
            catch (FormatException)
            {
                _logger.LogError("EVENT: ResendWebhookFailed — invalid webhook secret format");
                return StatusCode(500, "Webhook configuration error.");
            }

            var signedContent = Encoding.UTF8.GetBytes($"{svixId}.{svixTimestamp}.{rawBody}");
            var computedHmac  = Convert.ToBase64String(HMACSHA256.HashData(keyBytes, signedContent));

            // svix-signature may contain multiple space-separated "v1,<sig>" values.
            var verified = svixSignature
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(sig => sig.StartsWith("v1,", StringComparison.Ordinal)
                         && CryptographicOperations.FixedTimeEquals(
                                Encoding.UTF8.GetBytes(sig[3..]),
                                Encoding.UTF8.GetBytes(computedHmac)));

            if (!verified)
            {
                _logger.LogWarning("EVENT: ResendWebhookFailed — signature mismatch");
                return StatusCode(400, "Invalid webhook signature.");
            }
        }
        else
        {
            _logger.LogDebug("EVENT: ResendWebhookSignatureSkipped — Email:ResendWebhookSecret not configured");
        }

        ResendWebhookEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<ResendWebhookEvent>(
                rawBody,
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
