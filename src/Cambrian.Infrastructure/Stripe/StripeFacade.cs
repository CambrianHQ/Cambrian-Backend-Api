using Cambrian.Application.Interfaces;
using Stripe;
using Stripe.Checkout;

namespace Cambrian.Infrastructure.Stripe;

public class StripeFacade : IPaymentGateway
{
    public async Task<string> CreateCheckoutSessionAsync(
        int amountInCents,
        string productName,
        string? clientReferenceId = null,
        string? successUrl = null,
        string? cancelUrl = null)
    {
        var options = new SessionCreateOptions
        {
            Mode = "payment",
            SuccessUrl = successUrl ?? "https://cambrian.app/checkout/success?session_id={CHECKOUT_SESSION_ID}",
            CancelUrl = cancelUrl ?? "https://cambrian.app/checkout/cancel",
            ClientReferenceId = clientReferenceId,
            LineItems = new List<SessionLineItemOptions>
            {
                new()
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "usd",
                        UnitAmount = amountInCents,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = productName
                        }
                    },
                    Quantity = 1
                }
            }
        };

        var service = new SessionService();
        var session = await service.CreateAsync(options);

        return session.Url!;
    }

    public async Task<string> CreateSubscriptionCheckoutAsync(
        int amountInCents,
        string planName,
        string clientReferenceId,
        string successUrl,
        string cancelUrl)
    {
        // Stripe Accounts V2 requires a Customer object for Checkout sessions in test mode
        var customerService = new CustomerService();
        var customer = await customerService.CreateAsync(new CustomerCreateOptions
        {
            Metadata = new Dictionary<string, string>
            {
                { "cambrian_user_id", clientReferenceId }
            }
        });

        var options = new SessionCreateOptions
        {
            Mode = "subscription",
            Customer = customer.Id,
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            ClientReferenceId = clientReferenceId,
            LineItems = new List<SessionLineItemOptions>
            {
                new()
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "usd",
                        UnitAmount = amountInCents,
                        Recurring = new SessionLineItemPriceDataRecurringOptions
                        {
                            Interval = "month"
                        },
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = $"Cambrian {planName} Plan"
                        }
                    },
                    Quantity = 1
                }
            }
        };

        var service = new SessionService();
        var session = await service.CreateAsync(options);

        return session.Url!;
    }

    public async Task<Session> GetSessionAsync(string sessionId)
    {
        var service = new SessionService();
        return await service.GetAsync(sessionId);
    }
}