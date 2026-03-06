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
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();

        await _webhooks.HandleStripeAsync(json, Request.Headers["Stripe-Signature"]!);
        return MessageResponse("Received.");
    }
}