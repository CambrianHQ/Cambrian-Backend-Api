using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class WebhookService : IWebhookService
{
    public Task HandleStripeAsync(string payload)
    {
        return Task.CompletedTask;
    }
}
