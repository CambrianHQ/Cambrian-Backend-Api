namespace Cambrian.Application.Interfaces;

/// <summary>
/// Handles Stripe Connect webhook events (events that occur on artists' connected
/// accounts: tips, fan-subscription payments and lifecycle). Separate endpoint and
/// signing secret from the platform webhook.
/// </summary>
public interface IConnectWebhookService
{
    Task HandleStripeAsync(string payload, string signature);
}
