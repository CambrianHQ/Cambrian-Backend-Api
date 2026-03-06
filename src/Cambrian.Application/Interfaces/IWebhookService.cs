namespace Cambrian.Application.Interfaces;

public interface IWebhookService
{
    Task HandleStripeAsync(string payload, string signature);
}
