using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("webhook")]
public class WebhookController : ControllerBase
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
        var payload = await reader.ReadToEndAsync();
        await _webhooks.HandleStripeAsync(payload);
        return Ok();
    }
}
