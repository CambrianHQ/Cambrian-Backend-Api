using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("webhook")]
public class WebhookController : BaseController
{
    private readonly IWebhookService _webhooks;

    public WebhookController(IWebhookService webhooks)
    {
        _webhooks = webhooks;
    }

    [HttpPost("stripe")]
    public async Task<IActionResult> Stripe()
    {
        var signature = Request.Headers["Stripe-Signature"].FirstOrDefault();

        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();

        try
        {
            await _webhooks.HandleStripeAsync(json, signature ?? "");
            return MessageResponse("Received.");
        }
        catch (Stripe.StripeException)
        {
            return ErrorResponse("Invalid webhook signature.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("signature verification"))
        {
            return ErrorResponse("Webhook signature verification failed.");
        }
    }
}